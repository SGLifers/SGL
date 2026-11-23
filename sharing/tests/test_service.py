import copy
import io
import json
from pathlib import Path
import sys
import unittest
import uuid
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))
import app
from app import Service, challenge
from validation import Problem, digest, map_archive, mode


class MemoryRepository:
    def __init__(self):
        self.items, self.blobs, self.rates = {}, {}, {}
        self.configuration = {"clientId": "123456789", "clientSecret": "test-only"}

    def get(self, pk, sk="META"): return copy.deepcopy(self.items.get((pk, sk)))
    def put(self, item, absent=False):
        key = (item["pk"], item["sk"])
        if absent and key in self.items: raise Problem(409, "Already exists")
        self.items[key] = copy.deepcopy(item)
    def delete(self, pk, sk="META"): self.items.pop((pk, sk), None)
    def consume_login(self, pk):
        login = self.get(pk)
        if not login or login["status"] != "ready": raise Problem(409, "Already used")
        self.delete(pk)
        return login
    def claim_login(self, pk, now):
        login = self.get(pk)
        if not login or login["status"] != "pending" or login["expiresAt"] <= now: raise Problem(409, "Already processing")
        login["status"] = "processing"
        self.put(login)
        return login
    def publish(self, item, revision, expected, request):
        old = self.get(item["pk"])
        if (expected and (not old or old["latestRevisionId"] != expected)) or (not expected and old) or self.get(request["pk"]): raise Problem(409, "Conflict")
        self.put(item); self.put(revision); self.put(request)
    def listing(self, partition, cursor=None, owner=False):
        field, order = ("ownerPk", "ownerSk") if owner else ("listPk", "listSk")
        records = [copy.deepcopy(v) for v in self.items.values() if v.get(field) == partition]
        return sorted(records, key=lambda r: r[order], reverse=True), None
    def rate(self, key, ttl, limit):
        count = self.rates.get(key, 0)
        if count >= limit: raise Problem(429, "Limit reached")
        self.rates[key] = count + 1
    def secret(self): return copy.deepcopy(self.configuration)
    def upload_url(self, key, size, sha): return {"url": "https://bucket.s3.us-west-2.amazonaws.com/" + key, "headers": {"If-None-Match": "*"}}
    def read_blob(self, key, limit):
        data = self.blobs[key]
        if len(data) > limit: raise Problem(400, "Too large")
        return data
    def save_blob(self, key, data, content_type): self.blobs.setdefault(key, data)
    def download_url(self, key, filename=None): return "https://bucket.s3.us-west-2.amazonaws.com/" + key + "?temporary=test"


class FakeDiscord:
    def identify(self, code, verifier, redirect, configuration):
        if code != "valid": raise Problem(502, "Invalid code")
        return {"id": "123456789012345678", "global_name": "Lobby Host", "avatar": "abcdef"}


def map_bytes(world="World.cf1047"):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(world, b"test world data")
        archive.writestr("info.json", json.dumps({"name": "Test map", "author": "Original architect"}))
    return stream.getvalue()


class SharingTests(unittest.TestCase):
    def setUp(self):
        self.repo = MemoryRepository()
        self.time = 1_800_000_000
        self.service = Service(self.repo, "https://api.example.test", FakeDiscord(), lambda: self.time)
        self.user = {"pk": "USER#" + "a" * 32, "sk": "META", "id": "a" * 32, "discordId": "111111111", "displayName": "Creator", "avatarUrl": None}
        self.other = {**self.user, "id": "b" * 32, "pk": "USER#" + "b" * 32, "displayName": "Another creator"}
        self.repo.put(self.user); self.repo.put(self.other)
        self.mode = json.loads((Path(__file__).resolve().parents[2] / "src/SGL.Core/Data/tdm.json").read_text())
        self.mode["Options"]["CustomName"] = "Shared mode"

    def request(self, kind="mode", visibility="public", **extra):
        return {"requestId": uuid.uuid4().hex, "kind": kind, "title": "Test item", "visibility": visibility, "credits": "Original creator", "changeNote": "Initial publication", "content": {"payload": self.mode}, **extra}

    def publish_map(self, visibility="public", user=None):
        user = user or self.user
        data = map_bytes()
        upload = self.service.upload({"sha256": digest(data), "size": len(data)}, user)
        record = self.repo.get("UPLOAD#" + upload["uploadId"])
        self.repo.blobs[record["key"]] = data
        body = self.request("map", visibility, content={"uploadId": upload["uploadId"]})
        return self.service.publish(body, user)

    def assert_problem(self, status, action):
        with self.assertRaises(Problem) as raised: action()
        self.assertEqual(status, raised.exception.status)

    def test_discord_sign_in_pkce_and_one_time_exchange(self):
        verifier = "a" * 64
        start = self.service.start_auth({"challenge": challenge(verifier)}, "127.0.0.1")
        self.assertIn("code_challenge_method=S256", start["authorizationUrl"])
        self.assertIn("scope=identify", start["authorizationUrl"])
        self.assertEqual("pending", self.service.poll_auth({"requestId": start["requestId"], "verifier": verifier})["status"])
        self.assert_problem(401, lambda: self.service.poll_auth({"requestId": start["requestId"], "verifier": "b" * 64}))
        self.service.callback({"state": start["requestId"], "code": "valid"})
        result = self.service.poll_auth({"requestId": start["requestId"], "verifier": verifier})
        self.assertEqual("Lobby Host", result["user"]["displayName"])
        actor = self.service.actor({"authorization": "Bearer " + result["token"]}, True)
        self.assertEqual("123456789012345678", actor["discordId"])
        self.assert_problem(401, lambda: self.service.poll_auth({"requestId": start["requestId"], "verifier": verifier}))
        self.service.dispatch("POST", "/auth/logout", {}, {"authorization": "Bearer " + result["token"]}, {})
        self.assert_problem(401, lambda: self.service.actor({"authorization": "Bearer " + result["token"]}, True))

    def test_missing_configuration_and_expired_auth(self):
        self.repo.configuration = {}
        self.assert_problem(503, lambda: self.service.start_auth({"challenge": challenge("a" * 64)}, "ip"))
        self.repo.configuration = {"clientId": "id", "clientSecret": "secret"}
        self.service.secret_cache = None
        start = self.service.start_auth({"challenge": challenge("a" * 64)}, "ip")
        self.time += 601
        self.assert_problem(401, lambda: self.service.poll_auth({"requestId": start["requestId"], "verifier": "a" * 64}))
        self.assert_problem(400, lambda: self.service.callback({"state": start["requestId"], "code": "valid"}))

    def test_public_unlisted_private_listing_and_download(self):
        public = self.service.publish(self.request(), self.user)
        unlisted = self.service.publish(self.request(visibility="unlisted"), self.user)
        private = self.service.publish(self.request(visibility="private"), self.user)
        listing = self.service.dispatch("GET", "/items", {}, {}, {})
        self.assertEqual([public["itemId"]], [i["id"] for i in listing["items"]])
        self.assertEqual(unlisted["itemId"], self.service.item(unlisted["itemId"])["id"])
        self.assert_problem(404, lambda: self.service.item(private["itemId"]))
        self.assert_problem(404, lambda: self.service.item(private["itemId"], self.other))
        item = self.service.item(private["itemId"], self.user)
        result = self.service.download(item, self.service.revision(item, private["revisionId"]), self.user)
        self.assertEqual(digest(self.repo.blobs[result["revision"]["content"]["key"]]), result["download"]["sha256"])

    def test_stale_public_index_cannot_disclose_newly_private_metadata(self):
        first = self.service.publish(self.request(), self.user)
        stale = self.repo.get("ITEM#" + first["itemId"])
        self.service.publish(self.request(expectedRevisionId=first["revisionId"], visibility="private"), self.user, first["itemId"])
        self.repo.listing = lambda *args, **kwargs: ([stale], None)
        self.assertEqual([], self.service.dispatch("GET", "/items", {}, {}, {})["items"])

    def test_revisions_owner_attribution_conflicts_and_original_preservation(self):
        first = self.service.publish(self.request(), self.user)
        original = self.service.revision(self.service.item(first["itemId"]), first["revisionId"])
        body = self.request(expectedRevisionId=first["revisionId"], changeNote="Changed health")
        self.assert_problem(403, lambda: self.service.publish(body, self.other, first["itemId"]))
        second = self.service.publish(body, self.user, first["itemId"])
        self.assertEqual(original, self.service.revision(self.service.item(first["itemId"]), first["revisionId"]))
        latest = self.service.revision(self.service.item(first["itemId"]), second["revisionId"])
        self.assertEqual(self.user["id"], latest["actorId"])
        self.assertEqual(first["revisionId"], latest["parentRevisionId"])
        self.assert_problem(409, lambda: self.service.publish(self.request(expectedRevisionId=first["revisionId"]), self.user, first["itemId"]))

    def test_idempotent_publication_and_blob_deduplication(self):
        body = self.request()
        first = self.service.publish(body, self.user)
        self.assertEqual(first, self.service.publish(body, self.user))
        self.service.publish(self.request(), self.user)
        self.assertEqual(1, len(self.repo.blobs))
        body["title"] = "Different request"
        self.assert_problem(409, lambda: self.service.publish(body, self.user))

    def test_forks_keep_parent_credits(self):
        first = self.service.publish(self.request(), self.user)
        fork = self.service.publish(self.request(forkOf={"itemId": first["itemId"], "revisionId": first["revisionId"]}), self.other)
        item = self.service.item(fork["itemId"])
        self.assertEqual(self.other["id"], item["ownerId"])
        self.assertEqual(self.user["id"], item["forkOf"]["ownerId"])
        self.assertEqual("Original creator", item["forkOf"]["credits"])
        self.service.publish(self.request(expectedRevisionId=first["revisionId"], visibility="private", credits="Updated credits"), self.user, first["itemId"])
        later = self.service.publish(self.request(expectedRevisionId=fork["revisionId"]), self.other, fork["itemId"])
        self.assertEqual(item["forkOf"], self.service.item(later["itemId"])["forkOf"])

    def test_revision_metadata_is_historical_and_private_share_pages_are_generic(self):
        first = self.service.publish(self.request(title="Original title"), self.user)
        self.service.publish(self.request(title="New title", expectedRevisionId=first["revisionId"]), self.user, first["itemId"])
        item = self.service.item(first["itemId"])
        old = self.service.download(item, self.service.revision(item, first["revisionId"]), None)
        self.assertEqual("Original title", old["item"]["title"])
        private = self.service.publish(self.request(visibility="private", title="Never disclose"), self.user)
        page = self.service.share_page(private["itemId"], private["revisionId"])
        self.assertNotIn("Never disclose", page)
        self.assertIn("Open in SGL", page)

    def test_map_upload_integrity_ownership_and_validation(self):
        published = self.publish_map()
        item = self.service.item(published["itemId"])
        revision = self.service.revision(item, published["revisionId"])
        self.assertEqual("Original architect", revision["content"]["originalAuthor"])
        data = map_bytes(); upload = self.service.upload({"sha256": digest(data), "size": len(data)}, self.user)
        record = self.repo.get("UPLOAD#" + upload["uploadId"])
        self.repo.blobs[record["key"]] = b"wrong"
        self.assert_problem(400, lambda: self.service.publish(self.request("map", content={"uploadId": upload["uploadId"]}), self.user))
        self.assert_problem(403, lambda: self.service.publish(self.request("map", content={"uploadId": upload["uploadId"]}), self.other))
        self.assert_problem(400, lambda: map_archive(map_bytes("Prefab.cf1047_prefab")))
        self.assert_problem(400, lambda: map_archive(map_bytes("../World.cf1047")))

    def test_pinned_bundles_and_private_dependency_rules(self):
        map_item = self.publish_map()
        mode_item = self.service.publish(self.request(), self.user)
        refs = {"map": {"itemId": map_item["itemId"], "revisionId": map_item["revisionId"]}, "mode": {"itemId": mode_item["itemId"], "revisionId": mode_item["revisionId"]}}
        bundle = self.service.publish(self.request("bundle", content=refs), self.user)
        new_mode = self.service.publish(self.request(expectedRevisionId=mode_item["revisionId"]), self.user, mode_item["itemId"])
        item = self.service.item(bundle["itemId"])
        result = self.service.download(item, self.service.revision(item, bundle["revisionId"]), None)
        self.assertEqual(2, len(result["dependencies"]))
        self.assertEqual(mode_item["revisionId"], result["revision"]["content"]["mode"]["revisionId"])
        self.assertNotEqual(new_mode["revisionId"], mode_item["revisionId"])
        private = self.publish_map("private")
        refs["map"] = {"itemId": private["itemId"], "revisionId": private["revisionId"]}
        self.assert_problem(400, lambda: self.service.publish(self.request("bundle", content=refs), self.user))

    def test_html_escapes_user_content_and_pins_revision(self):
        published = self.service.publish(self.request(title='<script>alert("x")</script>'), self.user)
        page = self.service.share_page(published["itemId"], published["revisionId"])
        self.assertNotIn('<script>alert', page)
        self.assertIn('&lt;script&gt;', page)
        self.assertIn('revision=' + published["revisionId"], page)
        self.assertIn('sgl://library/install?', page)

    def test_write_routes_require_real_sessions_and_ignore_spoofed_actors(self):
        self.assert_problem(401, lambda: self.service.dispatch("POST", "/items", {}, {}, self.request(actorId=self.other["id"])))
        published = self.service.publish(self.request(actorId=self.other["id"], ownerId=self.other["id"]), self.user)
        item = self.service.item(published["itemId"])
        self.assertEqual(self.user["id"], item["ownerId"])

    def test_mode_version_rejects_boolean_and_unknown_bases(self):
        payload = self.request()["content"]["payload"]
        payload["Version"] = True
        self.assert_problem(400, lambda: mode(payload))
        payload["Version"] = 1
        payload["Variant"] = "PortalWarsGameVariant:Unknown"
        self.assert_problem(400, lambda: mode(payload))

    def test_handler_errors_do_not_disclose_credentials(self):
        app._service = self.service
        event = {"rawPath": "/items", "headers": {"Authorization": "Bearer never-print-this"}, "body": "{}", "requestContext": {"http": {"method": "POST"}}}
        result = app.handler(event, None)
        self.assertEqual(401, result["statusCode"])
        self.assertNotIn("never-print-this", result["body"])
        event["body"] = "invalid json"
        self.assertEqual(400, app.handler(event, None)["statusCode"])


if __name__ == "__main__": unittest.main()
