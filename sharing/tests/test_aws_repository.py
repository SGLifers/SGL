"""Exercise real boto3 serialization and DynamoDB transactions with local AWS emulation."""
import base64
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))
try:
    import boto3
    from moto import mock_aws
    AVAILABLE = True
except ImportError:
    AVAILABLE = False

from test_service import FakeDiscord, map_bytes
from validation import Problem, digest


@unittest.skipUnless(AVAILABLE, "Install sharing/requirements-dev.txt for AWS emulation tests")
class AwsRepositoryTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, {"AWS_ACCESS_KEY_ID": "testing", "AWS_SECRET_ACCESS_KEY": "testing", "AWS_DEFAULT_REGION": "us-west-2", "TABLE_NAME": "sgl-test", "BUCKET_NAME": "sgl-sharing-test-content", "DISCORD_SECRET_ARN": "sgl-discord-test"})
        self.environment.start()
        self.aws = mock_aws(); self.aws.start()
        template = json.loads((Path(__file__).resolve().parents[1] / "infra/template.json").read_text())
        properties = template["Resources"]["LibraryTable"]["Properties"]
        boto3.client("dynamodb").create_table(TableName="sgl-test", BillingMode="PAY_PER_REQUEST", AttributeDefinitions=properties["AttributeDefinitions"], KeySchema=properties["KeySchema"], GlobalSecondaryIndexes=properties["GlobalSecondaryIndexes"])
        boto3.client("s3").create_bucket(Bucket="sgl-sharing-test-content", CreateBucketConfiguration={"LocationConstraint": "us-west-2"})
        boto3.client("secretsmanager").create_secret(Name="sgl-discord-test", SecretString='{"clientId":"123456789","clientSecret":"test-only"}')
        from repository import AwsRepository
        from app import Service
        self.repo = AwsRepository(); self.service = Service(self.repo, "https://api.example.test", FakeDiscord(), lambda: 1_800_000_000)
        self.user = {"pk": "USER#" + "a" * 32, "sk": "META", "id": "a" * 32, "discordId": "111111111", "displayName": "Creator", "avatarUrl": None}
        self.repo.put(self.user)

    def tearDown(self):
        self.aws.stop(); self.environment.stop()

    def test_atomic_publish_query_conflict_and_immutable_blob(self):
        import uuid
        payload = json.loads((Path(__file__).resolve().parents[2] / "src/SGL.Core/Data/tdm.json").read_text())
        request = {"requestId": uuid.uuid4().hex, "title": "Test mode", "kind": "mode", "visibility": "public", "content": {"payload": payload}}
        first = self.service.publish(request, self.user)
        self.assertEqual(first, self.service.publish(request, self.user))
        public = self.service.dispatch("GET", "/items", {}, {}, {})
        self.assertEqual(first["itemId"], public["items"][0]["id"])
        revisions, _ = self.repo.listing("REVISIONS#" + first["itemId"])
        self.assertEqual(1, len(revisions))
        request["requestId"] = uuid.uuid4().hex
        request["expectedRevisionId"] = first["revisionId"]
        second = self.service.publish(request, self.user, first["itemId"])
        self.assertNotEqual(first["revisionId"], second["revisionId"])
        request["requestId"] = uuid.uuid4().hex
        with self.assertRaises(Problem): self.service.publish(request, self.user, first["itemId"])
        item = self.service.item(first["itemId"])
        original = self.service.revision(item, first["revisionId"])["content"]
        self.repo.save_blob(original["key"], b"do not replace", "application/json")
        self.assertNotEqual(b"do not replace", self.repo.read_blob(original["key"], 1_048_576))

    def test_presigned_upload_contract_and_map_publication(self):
        data = map_bytes()
        upload = self.service.upload({"sha256": digest(data), "size": len(data)}, self.user)
        self.assertEqual("*", upload["headers"]["If-None-Match"])
        self.assertEqual(base64.b64encode(bytes.fromhex(digest(data))).decode(), upload["headers"]["x-amz-checksum-sha256"])
        self.assertIn("X-Amz-SignedHeaders=", upload["url"])
        self.assertIn("content-length", upload["url"])
        from urllib.parse import urlsplit
        self.assertTrue(urlsplit(upload["url"]).hostname.endswith(".s3.us-west-2.amazonaws.com"))
        record = self.repo.get("UPLOAD#" + upload["uploadId"])
        self.repo.s3.put_object(Bucket=self.repo.bucket, Key=record["key"], Body=data)
        import uuid
        item = self.service.publish({"requestId": uuid.uuid4().hex, "title": "Test arena", "kind": "map", "content": {"uploadId": upload["uploadId"]}}, self.user)
        revision = self.service.revision(self.service.item(item["itemId"]), item["revisionId"])
        self.assertEqual(digest(data), revision["content"]["sha256"])

    def test_login_consumption_and_limits_are_conditional(self):
        self.repo.put({"pk": "LOGIN#test", "sk": "META", "status": "ready"})
        self.assertEqual("ready", self.repo.consume_login("LOGIN#test")["status"])
        with self.assertRaises(Problem): self.repo.consume_login("LOGIN#test")
        self.repo.rate("test", 1_900_000_000, 1)
        with self.assertRaises(Problem): self.repo.rate("test", 1_900_000_000, 1)


if __name__ == "__main__": unittest.main()
