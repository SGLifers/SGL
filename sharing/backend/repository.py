"""AWS persistence. Blob keys are immutable and never client-selected."""
import base64
import json
import os
from decimal import Decimal

from validation import Problem, require


def native(value):
    if isinstance(value, Decimal):
        return int(value) if value == value.to_integral_value() else float(value)
    if isinstance(value, dict):
        return {k: native(v) for k, v in value.items()}
    if isinstance(value, list):
        return [native(v) for v in value]
    return value


class AwsRepository:
    def __init__(self):
        import boto3
        from boto3.dynamodb.types import TypeSerializer
        from botocore.config import Config
        self.table = boto3.resource("dynamodb").Table(os.environ["TABLE_NAME"])
        self.ddb = boto3.client("dynamodb")
        self.serializer = TypeSerializer()
        self.s3 = boto3.client("s3", config=Config(signature_version="s3v4"))
        self.bucket = os.environ["BUCKET_NAME"]
        self.secrets = boto3.client("secretsmanager")

    def get(self, pk, sk="META"):
        return native(self.table.get_item(Key={"pk": pk, "sk": sk}, ConsistentRead=True).get("Item"))

    def put(self, item, absent=False):
        arguments = {"Item": item}
        if absent:
            arguments["ConditionExpression"] = "attribute_not_exists(pk)"
        self.table.put_item(**arguments)

    def delete(self, pk, sk="META"):
        self.table.delete_item(Key={"pk": pk, "sk": sk})

    def consume_login(self, pk):
        try:
            result = self.table.delete_item(Key={"pk": pk, "sk": "META"}, ConditionExpression="#s = :ready", ExpressionAttributeNames={"#s": "status"}, ExpressionAttributeValues={":ready": "ready"}, ReturnValues="ALL_OLD")
            return native(result.get("Attributes"))
        except self.ddb.exceptions.ConditionalCheckFailedException:
            raise Problem(409, "This sign-in request was already used.")

    def publish(self, item, revision, expected, request):
        serialize = lambda value: {key: self.serializer.serialize(v) for key, v in value.items()}
        put = {"TableName": self.table.name, "Item": serialize(item), "ConditionExpression": "attribute_not_exists(pk)"}
        if expected:
            put.update(ConditionExpression="latestRevisionId = :expected", ExpressionAttributeValues={":expected": {"S": expected}})
        try:
            self.ddb.transact_write_items(TransactItems=[{"Put": put}, {"Put": {"TableName": self.table.name, "Item": serialize(revision), "ConditionExpression": "attribute_not_exists(pk)"}}, {"Put": {"TableName": self.table.name, "Item": serialize(request), "ConditionExpression": "attribute_not_exists(pk)"}}])
        except self.ddb.exceptions.TransactionCanceledException:
            raise Problem(409, "This item changed while you were editing. Reload its latest revision before publishing.")

    def listing(self, partition, cursor=None, owner=False):
        from boto3.dynamodb.conditions import Key
        key = "ownerPk" if owner else "listPk"
        args = {"IndexName": "Owners" if owner else "Listings", "KeyConditionExpression": Key(key).eq(partition), "ScanIndexForward": False, "Limit": 50}
        if cursor:
            try:
                require(len(cursor) < 4096, "Invalid cursor.")
                args["ExclusiveStartKey"] = json.loads(base64.urlsafe_b64decode(cursor))
            except (ValueError, TypeError):
                raise Problem(400, "Invalid cursor.")
        result = self.table.query(**args)
        next_cursor = base64.urlsafe_b64encode(json.dumps(result["LastEvaluatedKey"]).encode()).decode() if "LastEvaluatedKey" in result else None
        return native(result.get("Items", [])), next_cursor

    def rate(self, key, ttl, limit):
        try:
            self.table.update_item(Key={"pk": "RATE#" + key, "sk": "META"}, UpdateExpression="SET #n = if_not_exists(#n, :zero) + :one, expiresAt = :ttl", ConditionExpression="attribute_not_exists(#n) OR #n < :limit", ExpressionAttributeNames={"#n": "count"}, ExpressionAttributeValues={":zero": 0, ":one": 1, ":ttl": ttl, ":limit": limit})
        except self.ddb.exceptions.ConditionalCheckFailedException:
            raise Problem(429, "Daily publishing or sign-in limit reached. Try again later.")

    def secret(self):
        return json.loads(self.secrets.get_secret_value(SecretId=os.environ["DISCORD_SECRET_ARN"])["SecretString"].lstrip("\ufeff"))

    def upload_url(self, key, size, sha):
        checksum = base64.b64encode(bytes.fromhex(sha)).decode()
        parameters = {"Bucket": self.bucket, "Key": key, "ContentType": "application/octet-stream", "ContentLength": size, "ChecksumSHA256": checksum, "IfNoneMatch": "*"}
        return {"url": self.s3.generate_presigned_url("put_object", Params=parameters, ExpiresIn=300), "headers": {"Content-Type": "application/octet-stream", "x-amz-checksum-sha256": checksum, "If-None-Match": "*"}}

    def read_blob(self, key, limit):
        response = self.s3.get_object(Bucket=self.bucket, Key=key)
        require(response["ContentLength"] <= limit, "Uploaded file exceeds its size limit.")
        with response["Body"] as body:
            data = body.read(limit + 1)
        require(len(data) <= limit, "Uploaded file exceeds its size limit.")
        return data

    def save_blob(self, key, data, content_type):
        try:
            self.s3.put_object(Bucket=self.bucket, Key=key, Body=data, ContentType=content_type, IfNoneMatch="*", ChecksumSHA256=base64.b64encode(__import__("hashlib").sha256(data).digest()).decode())
        except Exception as error:
            if getattr(error, "response", {}).get("Error", {}).get("Code") not in ("PreconditionFailed", "412"):
                raise

    def download_url(self, key, filename=None):
        args = {"Bucket": self.bucket, "Key": key}
        if filename:
            args["ResponseContentDisposition"] = 'attachment; filename="' + filename + '"'
        return self.s3.generate_presigned_url("get_object", Params=args, ExpiresIn=900)
