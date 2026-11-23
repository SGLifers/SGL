"""Latched sharing cutoff. Invoked only by the budget topic, reconciliation or IAM operators."""
import json
import os
import re
import time

STATE_KEY = {"pk": "CONTROL#BUDGET", "sk": "META"}
STOP_SID = "SglBudgetStopTransfers"


def baseline_policy(bucket):
    return {"Version": "2012-10-17", "Statement": [{"Sid": "RequireHTTPS", "Effect": "Deny", "Principal": "*", "Action": "s3:*", "Resource": [f"arn:aws:s3:::{bucket}", f"arn:aws:s3:::{bucket}/*"], "Condition": {"Bool": {"aws:SecureTransport": "false"}}}]}


def stop_statement(bucket):
    return {"Sid": STOP_SID, "Effect": "Deny", "Principal": "*", "Action": ["s3:GetObject", "s3:GetObjectVersion", "s3:PutObject"], "Resource": f"arn:aws:s3:::{bucket}/*"}


class AwsControls:
    def __init__(self):
        import boto3
        from botocore.config import Config
        # Bound each call so a stalled service does not consume the entire invocation
        # before the independent second cutoff is attempted. SNS/scheduler retry failures.
        config = Config(connect_timeout=2, read_timeout=3, retries={"total_max_attempts": 1, "mode": "standard"})
        self.table = boto3.resource("dynamodb", config=config).Table(os.environ["TABLE_NAME"])
        self.s3 = boto3.client("s3", config=config)
        self.functions = boto3.client("lambda", config=config)
        self.bucket = os.environ["BUCKET_NAME"]
        self.function = os.environ["API_FUNCTION_NAME"]

    def read(self):
        value = self.table.get_item(Key=STATE_KEY, ConsistentRead=True).get("Item", {})
        return {"paused": bool(value.get("paused", False)), "reason": str(value.get("reason", "")), "changedAt": int(value.get("changedAt", 0)), "notifications": list(value.get("notifications", []))}

    def write(self, value):
        self.table.put_item(Item={**STATE_KEY, **value})

    def policy(self):
        try:
            return json.loads(self.s3.get_bucket_policy(Bucket=self.bucket)["Policy"])
        except self.s3.exceptions.ClientError as error:
            if error.response["Error"]["Code"] != "NoSuchBucketPolicy":
                raise
            return baseline_policy(self.bucket)

    def transfers_stopped(self):
        statement = stop_statement(self.bucket)
        return any(s == statement for s in self.policy()["Statement"])

    def transfers(self, stopped):
        current = self.policy()
        updated = {**current, "Statement": [s for s in current["Statement"] if s.get("Sid") != STOP_SID]}
        if stopped:
            updated["Statement"].append(stop_statement(self.bucket))
        if not updated["Statement"]:
            updated = baseline_policy(self.bucket)
        if updated != current:
            self.s3.put_bucket_policy(Bucket=self.bucket, Policy=json.dumps(updated))

    def concurrency(self):
        return self.functions.get_function_concurrency(FunctionName=self.function).get("ReservedConcurrentExecutions")

    def api(self, stopped):
        desired = 0 if stopped else 2
        if self.concurrency() != desired:
            self.functions.put_function_concurrency(FunctionName=self.function, ReservedConcurrentExecutions=desired)


class Guard:
    def __init__(self, controls, topic, clock=time.time):
        self.controls, self.topic, self.clock = controls, topic, clock

    def status(self):
        state = self.controls.read()
        transfers = self.controls.transfers_stopped()
        concurrency = self.controls.concurrency()
        return {"paused": state["paused"] or transfers or concurrency == 0, "persistedPause": state["paused"], "transfersStopped": transfers, "apiConcurrency": concurrency, "reason": state["reason"], "changedAt": state["changedAt"]}

    def enforce(self):
        failures = []
        # Attempt both controls even when one AWS operation fails.
        for operation in (self.controls.transfers, self.controls.api):
            try:
                operation(True)
            except Exception:
                failures.append("control")
        if failures:
            raise RuntimeError("Sharing cutoff is incomplete. Check the budget guard alarms and retry pause.")

    def pause(self, reason, notification=None):
        failures = []
        try:
            state = self.controls.read()
        except Exception:
            state = {"notifications": []}
            failures.append("read")
        if notification and notification in state.get("notifications", []):
            if state.get("paused"):
                self.enforce()
            return self.status()
        events = state.get("notifications", []) + ([notification] if notification else [])
        state = {"paused": True, "reason": reason, "changedAt": int(self.clock()), "notifications": events[-256:]}
        try:
            self.controls.write(state)
        except Exception:
            failures.append("latch")
        self.enforce()
        if failures:
            raise RuntimeError("Sharing was stopped, but its persistent pause state needs verification. Retry pause.")
        return self.status()

    def resume(self, acknowledge):
        if acknowledge is not True:
            raise ValueError("Resume requires acknowledgeBudgetOverrun=true. Remaining charges and delayed budget alerts are possible.")
        state = self.controls.read()
        try:
            self.controls.transfers(False)
            self.controls.api(False)
            self.controls.write({**state, "paused": False, "reason": "operator-resume", "changedAt": int(self.clock())})
        except Exception:
            self.pause("resume-failed")
            raise RuntimeError("Resume failed. Sharing was paused again; review the guard alarms.") from None
        return self.status()

    def handle(self, event):
        if not isinstance(event, dict):
            raise ValueError("Invalid budget control event.")
        if "Records" in event:
            records = event["Records"]
            if not isinstance(records, list) or not records or any(not isinstance(r, dict) or r.get("EventSource") != "aws:sns" or not isinstance(r.get("Sns"), dict) or r["Sns"].get("TopicArn") != self.topic or not isinstance(r["Sns"].get("MessageId"), str) or not re.fullmatch(r"[A-Za-z0-9-]{1,128}", r["Sns"]["MessageId"]) for r in records):
                raise ValueError("Budget notification came from an unexpected source.")
            # This topic receives only the 100% alert. Do not parse AWS's email/message prose.
            for record in records:
                self.pause("monthly-budget-exceeded", record["Sns"]["MessageId"])
            return self.status()
        action = event.get("action")
        if action == "status":
            return self.status()
        if action == "pause":
            return self.pause("operator-pause")
        if action == "resume":
            return self.resume(event.get("acknowledgeBudgetOverrun"))
        if action == "reconcile":
            if self.controls.read()["paused"]:
                self.enforce()
            return {"reconciled": True}
        raise ValueError("Unknown budget control action.")


def handler(event, context):
    # One reserved execution serializes pause/resume/reconcile; no endpoint or anonymous invocation.
    return Guard(AwsControls(), os.environ["BUDGET_TOPIC_ARN"]).handle(event)
