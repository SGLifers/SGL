import copy
import io
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))
from budget_guard import AwsControls, Guard, STOP_SID, baseline_policy, stop_statement

TOPIC = "arn:aws:sns:us-west-2:111111111111:budget-cutoff"


def notification(identity="message-1", topic=TOPIC):
    return {"Records": [{"EventSource": "aws:sns", "Sns": {"TopicArn": topic, "MessageId": identity, "Message": "AWS notification text is not a machine-readable contract."}}]}


class Controls:
    def __init__(self):
        self.state = {"paused": False, "reason": "", "changedAt": 0, "notifications": []}
        self.stopped = False
        self.concurrent = 2
        self.fail = set()

    def read(self):
        if "read" in self.fail: raise OSError("read failed")
        return copy.deepcopy(self.state)

    def write(self, state):
        if "write" in self.fail: raise OSError("write failed")
        if "write-once" in self.fail:
            self.fail.remove("write-once")
            raise OSError("write failed once")
        self.state = copy.deepcopy(state)

    def transfers_stopped(self): return self.stopped
    def concurrency(self): return self.concurrent
    def transfers(self, stopped):
        if "transfers" in self.fail: raise OSError("S3 unavailable")
        self.stopped = stopped
    def api(self, stopped):
        if "api" in self.fail or not stopped and "resume-api" in self.fail: raise OSError("Lambda unavailable")
        self.concurrent = 0 if stopped else 2


class BudgetGuardTests(unittest.TestCase):
    def setUp(self):
        self.controls = Controls()
        self.now = 1_800_000_000
        self.guard = Guard(self.controls, TOPIC, lambda: self.now)

    def test_budget_notification_latches_api_and_existing_transfer_urls(self):
        status = self.guard.handle(notification())
        self.assertTrue(status["paused"])
        self.assertTrue(status["persistedPause"])
        self.assertTrue(status["transfersStopped"])
        self.assertEqual(0, status["apiConcurrency"])
        self.assertEqual("monthly-budget-exceeded", status["reason"])
        self.assertEqual(["message-1"], self.controls.state["notifications"])

    def test_reconcile_repairs_drift_without_ever_resuming(self):
        self.guard.handle(notification())
        self.controls.stopped = False; self.controls.concurrent = 2
        self.now += 40 * 86_400  # A new billing month must not reopen sharing.
        self.guard.handle({"action": "reconcile"})
        self.assertTrue(self.controls.stopped)
        self.assertEqual(0, self.controls.concurrent)
        self.assertTrue(self.controls.state["paused"])

    def test_explicit_resume_and_duplicate_delivery_after_resume(self):
        self.guard.handle(notification())
        for acknowledge in (None, False, "true", 1):
            with self.assertRaises(ValueError): self.guard.handle({"action": "resume", "acknowledgeBudgetOverrun": acknowledge})
        result = self.guard.handle({"action": "resume", "acknowledgeBudgetOverrun": True})
        self.assertFalse(result["paused"])
        self.assertEqual(2, result["apiConcurrency"])
        self.guard.handle(notification())
        self.assertFalse(self.controls.state["paused"])
        self.assertFalse(self.controls.stopped)
        self.guard.handle(notification("new-budget-breach"))
        self.assertTrue(self.controls.stopped)

    def test_foreign_or_malformed_notifications_never_change_controls(self):
        for event in (notification(topic="arn:aws:sns:us-west-2:111111111111:other"), {"Records": []}, {"Records": [None]}, {"Records": [{"EventSource": "aws:sns", "Sns": {}}]}, {"action": "enable"}):
            with self.assertRaises(ValueError): self.guard.handle(event)
        self.assertFalse(self.controls.state["paused"])
        self.assertEqual(2, self.controls.concurrent)

    def test_partial_cutoff_attempts_both_controls_and_retries(self):
        for failed in ("api", "transfers"):
            with self.subTest(failed=failed):
                controls = Controls(); controls.fail.add(failed)
                guard = Guard(controls, TOPIC)
                with self.assertRaises(RuntimeError): guard.handle(notification())
                self.assertTrue(controls.state["paused"])
                self.assertEqual(0 if failed == "transfers" else 2, controls.concurrent)
                self.assertEqual(failed == "api", controls.stopped)
                controls.fail.clear(); guard.handle(notification())
                self.assertEqual(0, controls.concurrent)
                self.assertTrue(controls.stopped)

    def test_datastore_failure_still_stops_traffic_and_reports_failure(self):
        for failed in ("read", "write"):
            controls = Controls(); controls.fail.add(failed)
            with self.assertRaises(RuntimeError): Guard(controls, TOPIC).handle(notification())
            self.assertTrue(controls.stopped)
            self.assertEqual(0, controls.concurrent)

    def test_failed_resume_rolls_back_to_paused(self):
        for failed in ("resume-api", "write-once"):
            controls = Controls(); guard = Guard(controls, TOPIC)
            guard.handle(notification()); controls.fail.add(failed)
            with self.assertRaises(RuntimeError): guard.handle({"action": "resume", "acknowledgeBudgetOverrun": True})
            self.assertTrue(controls.state["paused"])
            self.assertTrue(controls.stopped)
            self.assertEqual(0, controls.concurrent)

    def test_status_detects_controls_even_when_latch_is_absent(self):
        self.controls.concurrent = 0
        status = self.guard.handle({"action": "status"})
        self.assertTrue(status["paused"])
        self.assertFalse(status["persistedPause"])

    def test_template_wires_thresholds_cutoff_retry_and_failure_alarms(self):
        template = json.loads((Path(__file__).resolve().parents[1] / "infra/template.json").read_text())
        resources = template["Resources"]
        budget = resources["MonthlyBudget"]
        self.assertNotIn("Condition", budget)
        notices = budget["Properties"]["NotificationsWithSubscribers"]
        self.assertEqual([50, 80, 100], [n["Notification"]["Threshold"] for n in notices])
        self.assertTrue(all(n["Notification"]["NotificationType"] == "ACTUAL" for n in notices))
        self.assertEqual([0, 0, 1], [sum(s["SubscriptionType"] == "SNS" for s in n["Subscribers"]) for n in notices])
        self.assertTrue(all(any(s["SubscriptionType"] == "EMAIL" for s in n["Subscribers"]) for n in notices))
        self.assertNotIn("Default", template["Parameters"]["BudgetEmail"])
        self.assertEqual(1, resources["BudgetGuardFunction"]["Properties"]["ReservedConcurrentExecutions"])
        self.assertEqual("rate(5 minutes)", resources["BudgetReconcileRule"]["Properties"]["ScheduleExpression"])
        self.assertEqual({"Fn::If": ["SharingIsPaused", 0, 2]}, resources["ApiFunction"]["Properties"]["ReservedConcurrentExecutions"])
        self.assertEqual(6, sum(r["Type"] == "AWS::CloudWatch::Alarm" for r in resources.values()))
        self.assertEqual({"Fn::GetAtt": ["BudgetFailureQueue", "Arn"]}, resources["BudgetGuardAsyncFailures"]["Properties"]["DestinationConfig"]["OnFailure"]["Destination"])
        policy = resources["BudgetGuardRole"]["Properties"]["Policies"][0]["PolicyDocument"]["Statement"]
        concurrency = next(s for s in policy if "lambda:PutFunctionConcurrency" in s["Action"])
        self.assertEqual({"Fn::GetAtt": ["ApiFunction", "Arn"]}, concurrency["Resource"])
        self.assertFalse(any("DeleteObject" in json.dumps(s) for s in policy))
        app_policy = resources["FunctionRole"]["Properties"]["Policies"][0]["PolicyDocument"]["Statement"]
        self.assertTrue(any(s["Effect"] == "Deny" and s.get("Condition", {}).get("ForAnyValue:StringLike", {}).get("dynamodb:LeadingKeys") == ["CONTROL#*"] for s in app_policy))


try:
    import boto3
    from moto import mock_aws
    AWS_AVAILABLE = True
except ImportError:
    AWS_AVAILABLE = False


@unittest.skipUnless(AWS_AVAILABLE, "Install sharing/requirements-dev.txt for AWS emulation tests")
class AwsBudgetControlsTests(unittest.TestCase):
    def test_real_sdk_latch_policy_preservation_and_concurrency(self):
        with patch.dict(os.environ, {"AWS_ACCESS_KEY_ID": "testing", "AWS_SECRET_ACCESS_KEY": "testing", "AWS_DEFAULT_REGION": "us-west-2", "TABLE_NAME": "budget-test", "BUCKET_NAME": "sgl-budget-test", "API_FUNCTION_NAME": "sgl-api-test"}), mock_aws():
            ddb = boto3.client("dynamodb")
            ddb.create_table(TableName="budget-test", BillingMode="PAY_PER_REQUEST", AttributeDefinitions=[{"AttributeName": k, "AttributeType": "S"} for k in ("pk", "sk")], KeySchema=[{"AttributeName": "pk", "KeyType": "HASH"}, {"AttributeName": "sk", "KeyType": "RANGE"}])
            s3 = boto3.client("s3")
            s3.create_bucket(Bucket="sgl-budget-test", CreateBucketConfiguration={"LocationConstraint": "us-west-2"})
            policy = baseline_policy("sgl-budget-test")
            policy["Statement"].append({"Sid": "KeepUnrelatedRule", "Effect": "Deny", "Principal": "*", "Action": "s3:DeleteObject", "Resource": "arn:aws:s3:::sgl-budget-test/*"})
            s3.put_bucket_policy(Bucket="sgl-budget-test", Policy=json.dumps(policy))
            role = boto3.client("iam").create_role(RoleName="budget-api-test", AssumeRolePolicyDocument=json.dumps({"Version": "2012-10-17", "Statement": [{"Effect": "Allow", "Principal": {"Service": "lambda.amazonaws.com"}, "Action": "sts:AssumeRole"}]}))["Role"]["Arn"]
            archive = io.BytesIO()
            with zipfile.ZipFile(archive, "w") as zip_file: zip_file.writestr("app.py", "def handler(event, context): return {}")
            functions = boto3.client("lambda")
            functions.create_function(FunctionName="sgl-api-test", Runtime="python3.13", Role=role, Handler="app.handler", Code={"ZipFile": archive.getvalue()})
            functions.put_function_concurrency(FunctionName="sgl-api-test", ReservedConcurrentExecutions=2)
            controls = AwsControls(); guard = Guard(controls, TOPIC)
            self.assertTrue(guard.handle(notification())["paused"])
            current = json.loads(s3.get_bucket_policy(Bucket="sgl-budget-test")["Policy"])
            self.assertIn(stop_statement("sgl-budget-test"), current["Statement"])
            self.assertEqual(0, functions.get_function_concurrency(FunctionName="sgl-api-test")["ReservedConcurrentExecutions"])
            # State is persisted outside the process, with DynamoDB's actual serializer.
            self.assertTrue(Guard(AwsControls(), TOPIC).status()["persistedPause"])
            guard.handle({"action": "resume", "acknowledgeBudgetOverrun": True})
            self.assertEqual(policy, json.loads(s3.get_bucket_policy(Bucket="sgl-budget-test")["Policy"]))
            self.assertEqual(2, functions.get_function_concurrency(FunctionName="sgl-api-test")["ReservedConcurrentExecutions"])
