"""Explicit live checks. Uses a named AWS profile; never prints signed URLs or secrets."""
import argparse
import json
import sys
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET

import boto3
from botocore.config import Config


class SmokeFailure(RuntimeError):
    pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--account-id", required=True)
    parser.add_argument("--region", default="us-west-2")
    parser.add_argument("--stack-name", default="sgl-sharing")
    parser.add_argument("--exercise-cutoff", action="store_true", help="Pauses and restores this stack; use before community rollout.")
    args = parser.parse_args()
    session = boto3.Session(profile_name=args.profile, region_name=args.region)
    if session.client("sts").get_caller_identity()["Account"] != args.account_id:
        raise SmokeFailure("Wrong AWS account; no changes made.")
    outputs = session.client("cloudformation").describe_stacks(StackName=args.stack_name)["Stacks"][0]["Outputs"]
    outputs = {o["OutputKey"]: o["OutputValue"] for o in outputs}
    functions = session.client("lambda")
    s3 = session.client("s3", config=Config(signature_version="s3v4", s3={"addressing_style": "virtual"}))
    table = session.resource("dynamodb").Table(outputs["TableName"])
    state_key = {"pk": "CONTROL#BUDGET", "sk": "META"}

    def control(action, **kwargs):
        result = functions.invoke(FunctionName=outputs["BudgetControlFunctionName"], Payload=json.dumps({"action": action, **kwargs}).encode())
        value = json.loads(result["Payload"].read())
        if result.get("FunctionError"):
            raise SmokeFailure("Budget control invocation failed; inspect its CloudWatch logs.")
        return value

    def http(url, data=None, headers=None):
        request = urllib.request.Request(url, data=data, headers=headers or {}, method="PUT" if data is not None else "GET")
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                return response.status, response.read(65_536)
        except urllib.error.HTTPError as error:
            code = error.code
            body = error.read(65_536)
            error.close()
            return code, body
        except urllib.error.URLError:
            raise SmokeFailure("HTTP smoke request failed.") from None

    def expect(url, status, data=None):
        for _ in range(30):
            actual, body = http(url, data, {"Content-Type": "application/octet-stream"} if data is not None else {})
            if actual == status:
                return body
            if actual == 400:
                try: reason = ET.fromstring(body).findtext("Code") or "BadRequest"
                except ET.ParseError: reason = "BadRequest"
                raise SmokeFailure("HTTP 400 during smoke check: " + reason)
            time.sleep(2)
        raise SmokeFailure(f"HTTP check expected {status}; received {actual}.")

    status = control("status")
    if status["paused"]:
        raise SmokeFailure("Sharing is already paused. Smoke checks will not resume an existing cutoff.")
    health_url = outputs["ApiUrl"].rstrip("/") + "/health"
    health = json.loads(expect(health_url, 200))
    expect(outputs["ApiUrl"].rstrip("/") + "/items", 200)
    expect(outputs["ApiUrl"].rstrip("/") + "/me", 401)
    print("Public health/library and authenticated-route rejection passed. Discord configured:", health["authConfigured"], flush=True)
    if not args.exercise_cutoff:
        return

    bucket = outputs["ContentBucketName"]
    key = "staging/smoke/" + uuid.uuid4().hex
    put_key = key + "-upload"
    contents = b"SGL deployment cutoff smoke test"
    s3.put_object(Bucket=bucket, Key=key, Body=contents)
    download = s3.generate_presigned_url("get_object", Params={"Bucket": bucket, "Key": key}, ExpiresIn=900)
    upload = s3.generate_presigned_url("put_object", Params={"Bucket": bucket, "Key": put_key, "ContentType": "application/octet-stream"}, ExpiresIn=900)
    initial = table.get_item(Key=state_key, ConsistentRead=True).get("Item", {})
    allowed_events = set(initial.get("notifications", []))
    owns_pause = False
    try:
        expect(download, 200)
        expect(upload, 200, contents)
        owns_pause = True
        control("pause")
        expect(health_url, 403)
        expect(download, 403)
        expect(upload, 403, contents)
        print("Manual cutoff blocked the API and previously issued S3 GET/PUT URLs.", flush=True)

        def resume_test_pause():
            current = table.get_item(Key=state_key, ConsistentRead=True).get("Item", {})
            if set(current.get("notifications", [])) - allowed_events:
                raise SmokeFailure("A separate budget event arrived. Sharing remains paused for operator review.")
            restored = control("resume", acknowledgeBudgetOverrun=True)
            if restored["paused"]:
                raise SmokeFailure("Resume did not restore sharing.")
            expect(health_url, 200)
            expect(download, 200)
            expect(upload, 200, contents)

        resume_test_pause()
        owns_pause = False
        configuration = functions.get_function_configuration(FunctionName=outputs["BudgetControlFunctionName"])
        topic_arn = configuration["Environment"]["Variables"]["BUDGET_TOPIC_ARN"]
        published = session.client("sns").publish(TopicArn=topic_arn, Message="SGL deployment cutoff delivery test; no real budget breach.")
        allowed_events.add(published["MessageId"])
        owns_pause = True
        for _ in range(45):
            if control("status")["paused"]:
                break
            time.sleep(2)
        else:
            raise SmokeFailure("SNS cutoff was not observed within 90 seconds.")
        expect(health_url, 403)
        expect(download, 403)
        expect(upload, 403, contents)
        print("SNS delivery invoked the cutoff and blocked API/S3 transfers.", flush=True)
        resume_test_pause()
        owns_pause = False
        print("Sharing restored; both functions remain in their configured concurrency mode.", flush=True)
    finally:
        if owns_pause:
            current = table.get_item(Key=state_key, ConsistentRead=True).get("Item", {})
            if not (set(current.get("notifications", [])) - allowed_events):
                control("resume", acknowledgeBudgetOverrun=True)
        # Only synthetic, uniquely named smoke files; no player content is touched.
        for path in (key, put_key):
            versions = s3.list_object_versions(Bucket=bucket, Prefix=path)
            for version in versions.get("Versions", []) + versions.get("DeleteMarkers", []):
                if version["Key"] == path:
                    s3.delete_object(Bucket=bucket, Key=path, VersionId=version["VersionId"])


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Exception representations can contain signed URL query credentials.
        print("Deployed smoke check failed:", str(error) if isinstance(error, SmokeFailure) else type(error).__name__, file=sys.stderr)
        sys.exit(1)
