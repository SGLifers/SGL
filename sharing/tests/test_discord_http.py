import contextlib
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import urllib.error
import urllib.parse

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))
from app import Discord, Service, challenge
from validation import Problem
from test_service import MemoryRepository, FakeDiscord


class DiscordHttpTests(unittest.TestCase):
    configuration = {"clientId": "123456789", "clientSecret": "never-log-secret"}

    @staticmethod
    def response(value): return io.BytesIO(json.dumps(value).encode())

    def test_real_adapter_posts_form_then_fetches_profile_with_documented_agent(self):
        with patch("urllib.request.urlopen", side_effect=[self.response({"access_token": "never-log-token"}), self.response({"id": "123456789", "username": "Host"})]) as opener:
            profile = Discord().identify("never-log-code", "a" * 64, "https://service.test/auth/callback", self.configuration)
        self.assertEqual("123456789", profile["id"])
        request = opener.call_args_list[0].args[0]
        form = urllib.parse.parse_qs(request.data.decode())
        self.assertEqual(["authorization_code"], form["grant_type"])
        self.assertEqual(["never-log-secret"], form["client_secret"])
        self.assertEqual(["a" * 64], form["code_verifier"])
        self.assertEqual(["https://service.test/auth/callback"], form["redirect_uri"])
        self.assertEqual("application/x-www-form-urlencoded", request.get_header("Content-type"))
        self.assertTrue(request.get_header("User-agent").startswith("DiscordBot (https://github.com/SGLifers/SGL,"))
        self.assertEqual("Bearer never-log-token", opener.call_args_list[1].args[0].get_header("Authorization"))

    def test_rejected_client_is_actionable_without_logging_raw_response_or_credentials(self):
        failure = urllib.error.HTTPError("https://discord.com/api/oauth2/token?code=never-log-code", 401, "never-log-reason", {}, self.response({"error": "invalid_client", "error_description": "never-log-secret never-log-token"}))
        output = io.StringIO()
        with patch("urllib.request.urlopen", side_effect=failure), contextlib.redirect_stdout(output), self.assertRaises(Problem) as raised:
            Discord().identify("never-log-code", "a" * 64, "https://service.test/auth/callback", self.configuration)
        self.assertIn("administrator", str(raised.exception))
        self.assertIn("token 401 invalid_client", output.getvalue())
        for value in ("never-log-secret", "never-log-token", "never-log-code", "never-log-reason"):
            self.assertNotIn(value, output.getvalue() + str(raised.exception))

    def test_profile_error_identifies_the_failed_phase(self):
        failure = urllib.error.HTTPError("https://discord.com/api/v10/users/@me", 403, "Forbidden", {}, self.response({"message": "private upstream details"}))
        output = io.StringIO()
        with patch("urllib.request.urlopen", side_effect=[self.response({"access_token": "private-token"}), failure]), contextlib.redirect_stdout(output), self.assertRaises(Problem) as raised:
            Discord().identify("code", "a" * 64, "https://service.test/auth/callback", self.configuration)
        self.assertIn("profile request failed (HTTP 403)", str(raised.exception))
        self.assertNotIn("private", output.getvalue())

    def test_pkce_error_logs_only_a_fixed_hint(self):
        failure = urllib.error.HTTPError("https://discord.com/api/oauth2/token", 400, "bad", {}, self.response({"error": "invalid_grant", "error_description": "code_verifier wrong: private-verifier"}))
        output = io.StringIO()
        with patch("urllib.request.urlopen", side_effect=failure), contextlib.redirect_stdout(output), self.assertRaises(Problem):
            Discord().identify("code", "a" * 64, "https://service.test/auth/callback", self.configuration)
        self.assertIn("invalid_grant pkce", output.getvalue())
        self.assertNotIn("private-verifier", output.getvalue())

    def test_callback_failure_stops_desktop_polling_and_drops_discord_verifier(self):
        repo = MemoryRepository()
        service = Service(repo, "https://service.test", FakeDiscord())
        verifier = "a" * 64
        start = service.start_auth({"challenge": challenge(verifier)}, "test-ip")
        with self.assertRaises(Problem): service.callback({"state": start["requestId"], "code": "invalid"})
        login = repo.get("LOGIN#" + start["requestId"])
        self.assertEqual("failed", login["status"])
        self.assertNotIn("discordVerifier", login)
        with self.assertRaises(Problem) as raised: service.poll_auth({"requestId": start["requestId"], "verifier": verifier})
        self.assertEqual(502, raised.exception.status)

    def test_duplicate_callback_cannot_overwrite_a_completed_sign_in(self):
        repo = MemoryRepository()
        service = Service(repo, "https://service.test", FakeDiscord())
        verifier = "a" * 64
        start = service.start_auth({"challenge": challenge(verifier)}, "test-ip")
        service.callback({"state": start["requestId"], "code": "valid"})
        with self.assertRaises(Problem): service.callback({"state": start["requestId"], "code": "valid"})
        self.assertEqual("ready", repo.get("LOGIN#" + start["requestId"])["status"])
