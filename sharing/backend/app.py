"""SGL sharing API. The Lambda deployment never enables a development auth bypass."""
import base64
import hashlib
import html
import json
import os
import re
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone

from validation import MAX_MAP, Problem, digest, identity, json_bytes, map_archive, mode, require, tags, text


def challenge(verifier):
    return base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")


class Discord:
    user_agent = "DiscordBot (https://github.com/SGLifers/SGL, 0.3.1)"

    def request_json(self, stage, request):
        try:
            with urllib.request.urlopen(request, timeout=12) as response:
                value = json.load(response)
                if not isinstance(value, dict):
                    raise ValueError("Expected object")
                return value
        except urllib.error.HTTPError as error:
            oauth_error = "upstream_error"
            hint = "none"
            try:
                body = json.loads(error.read(65_536))
                if isinstance(body.get("error"), str) and body["error"] in {"invalid_client", "invalid_grant", "invalid_request", "invalid_scope", "unauthorized_client", "unsupported_grant_type", "access_denied"}:
                    oauth_error = body["error"]
                description = body.get("error_description", "")
                if isinstance(description, str):
                    if "code_verifier" in description or "code_challenge" in description: hint = "pkce"
                    elif "redirect_uri" in description: hint = "redirect_uri"
            except (ValueError, UnicodeError, AttributeError):
                pass
            finally:
                error.close()
            # Only fixed phase labels, HTTP status and allowlisted OAuth identifiers.
            # Never log response descriptions, codes, tokens, credentials or headers.
            print("SGL Discord request failed", stage, error.code, oauth_error, hint)
            if oauth_error == "invalid_client":
                message = "Discord rejected SGL's application credentials. The SGL administrator needs to update its Discord sign-in configuration."
            elif oauth_error == "invalid_grant":
                message = "Discord rejected this sign-in code. Cancel the waiting sign-in and start a new one from SGL."
            else:
                message = f"Discord {stage} request failed (HTTP {error.code}). Start sign-in again."
            raise Problem(502, message) from None
        except (urllib.error.URLError, TimeoutError):
            print("SGL Discord request failed", stage, "network_error")
            raise Problem(502, "Discord could not be reached during sign-in. Start sign-in again.") from None
        except (ValueError, UnicodeError):
            print("SGL Discord request failed", stage, "invalid_response")
            raise Problem(502, "Discord returned an invalid sign-in response. Start sign-in again.") from None

    def identify(self, code, verifier, redirect, configuration):
        form = urllib.parse.urlencode({"client_id": configuration["clientId"], "client_secret": configuration["clientSecret"], "grant_type": "authorization_code", "code": code, "redirect_uri": redirect, "code_verifier": verifier}).encode()
        try:
            request = urllib.request.Request("https://discord.com/api/oauth2/token", data=form, headers={"Content-Type": "application/x-www-form-urlencoded", "User-Agent": self.user_agent, "Accept": "application/json"})
            token = self.request_json("token", request)
            request = urllib.request.Request("https://discord.com/api/v10/users/@me", headers={"Authorization": "Bearer " + token["access_token"], "User-Agent": self.user_agent, "Accept": "application/json"})
            profile = self.request_json("profile", request)
            require(isinstance(profile.get("id"), str) and re.fullmatch(r"\d{5,24}", profile["id"]), "Discord returned an invalid identity.", 502)
            return profile
        except (KeyError, ValueError, TypeError):
            print("SGL Discord request failed", "response_shape")
            raise Problem(502, "Discord returned an incomplete sign-in response. Start sign-in again.") from None


class Service:
    def __init__(self, repository, base_url, discord=None, clock=time.time):
        self.repo = repository
        self.base = base_url.rstrip("/")
        self.discord = discord or Discord()
        self.clock = clock
        self.secret_cache = None
        self.secret_checked = 0

    def now(self):
        return int(self.clock())

    def stamp(self):
        return datetime.fromtimestamp(self.clock(), timezone.utc).isoformat(timespec="microseconds")

    def configuration(self):
        if self.secret_cache is None or self.now() - self.secret_checked > 60:
            self.secret_cache = self.repo.secret()
            self.secret_checked = self.now()
        return self.secret_cache

    def actor(self, headers, required=False):
        authorization = headers.get("authorization", "")
        if not authorization:
            require(not required, "Sign in with Discord first.", 401)
            return None
        require(authorization.startswith("Bearer ") and len(authorization) < 300, "Invalid session.", 401)
        session = self.repo.get("SESSION#" + digest(authorization[7:].encode()))
        require(session and session["expiresAt"] > self.now(), "Your session expired. Sign in again.", 401)
        user = self.repo.get("USER#" + session["userId"])
        require(user, "Your session is no longer available.", 401)
        return user

    def item(self, item_id, actor=None):
        item = self.repo.get("ITEM#" + identity(item_id))
        require(item and (item["visibility"] != "private" or actor and actor["id"] == item["ownerId"]), "Item not found or unavailable to this account.", 404)
        return item

    def revision(self, item, revision_id):
        revision = self.repo.get("ITEM#" + item["id"], "REV#" + identity(revision_id))
        require(revision, "Revision not found.", 404)
        return revision

    def public_user(self, user):
        return {k: user.get(k) for k in ("id", "discordId", "displayName", "avatarUrl")}

    def public_item(self, item, actor=None, include_preview=False):
        result = {k: item.get(k) for k in ("id", "kind", "title", "description", "visibility", "ownerId", "credits", "source", "forkOf", "latestRevisionId", "revisionNumber", "createdAt", "updatedAt")}
        owner = self.repo.get("USER#" + item["ownerId"])
        result["owner"] = self.public_user(owner) if owner else {"id": item["ownerId"], "displayName": "Former member"}
        result["shareUrl"] = self.base + "/share/" + item["id"]
        result["tags"] = item.get("tags", [])
        if include_preview:
            result["preview"] = self.preview(item, self.revision(item, item["latestRevisionId"]), actor) if item["kind"] in ("map", "bundle") else None
        return result

    def preview(self, item, revision, actor=None):
        if item["kind"] == "bundle":
            ref = revision["content"]["map"]
            try:
                dependency = self.item(ref["itemId"], actor)
                return self.preview(dependency, self.revision(dependency, ref["revisionId"]), actor)
            except Problem as error:
                if error.status == 404:
                    return None
                raise
        image = revision["content"].get("preview") if item["kind"] == "map" else None
        if not image:
            return None
        return {"url": self.repo.download_url(image["key"]), "sha256": image["sha256"], "size": image["size"]}

    def public_revision(self, revision):
        result = {k: revision.get(k) for k in ("id", "itemId", "number", "parentRevisionId", "actorId", "createdAt", "changeNote", "content", "metadata")}
        actor = self.repo.get("USER#" + revision["actorId"])
        result["actor"] = self.public_user(actor) if actor else {"id": revision["actorId"], "displayName": "Former member"}
        return result

    def rate(self, purpose, subject, limit, period=86_400):
        bucket = self.now() // period
        self.repo.rate(f"{purpose}#{subject}#{bucket}", (bucket + 2) * period, limit)

    def start_auth(self, body, ip):
        config = self.configuration()
        require(config.get("clientId") and config.get("clientSecret"), "Discord sign-in has not been configured by the SGL administrator yet.", 503)
        desktop_challenge = body.get("challenge", "")
        require(isinstance(desktop_challenge, str) and re.fullmatch(r"[A-Za-z0-9_-]{43}", desktop_challenge), "Invalid sign-in challenge.")
        self.rate("login", digest(ip.encode())[:24], 20, 3600)
        request_id, verifier = secrets.token_urlsafe(32), secrets.token_urlsafe(48)
        self.repo.put({"pk": "LOGIN#" + request_id, "sk": "META", "challenge": desktop_challenge, "discordVerifier": verifier, "status": "pending", "expiresAt": self.now() + 600}, absent=True)
        query = urllib.parse.urlencode({"response_type": "code", "client_id": config["clientId"], "scope": "identify", "redirect_uri": self.base + "/auth/callback", "state": request_id, "code_challenge": challenge(verifier), "code_challenge_method": "S256"})
        return {"requestId": request_id, "authorizationUrl": "https://discord.com/oauth2/authorize?" + query, "expiresIn": 600}

    def callback(self, query):
        request_id = query.get("state", "")
        require(isinstance(request_id, str) and re.fullmatch(r"[A-Za-z0-9_-]{43}", request_id), "Invalid sign-in state.")
        login = self.repo.get("LOGIN#" + request_id)
        require(login and login["expiresAt"] > self.now() and login["status"] == "pending", "Sign-in expired or was already completed. Start again.")
        login = self.repo.claim_login("LOGIN#" + request_id, self.now())
        try:
            require(not query.get("error") and query.get("code"), "Discord sign-in was declined.")
            profile = self.discord.identify(query["code"], login["discordVerifier"], self.base + "/auth/callback", self.configuration())
        except Problem as error:
            login.update(status="failed", failure=str(error))
            login.pop("discordVerifier", None)
            self.repo.put(login)
            raise
        user_id = uuid.uuid5(uuid.NAMESPACE_URL, "https://discord.com/users/" + profile["id"]).hex
        avatar = profile.get("avatar")
        avatar_url = "https://cdn.discordapp.com/avatars/" + profile["id"] + "/" + avatar + ".png?size=128" if isinstance(avatar, str) and re.fullmatch(r"[A-Za-z0-9_]+", avatar) else None
        user = {"pk": "USER#" + user_id, "sk": "META", "id": user_id, "discordId": profile["id"], "displayName": text(profile.get("global_name") or profile.get("username") or "Discord member", "display name", 100), "avatarUrl": avatar_url}
        self.repo.put(user)
        login.update(status="ready", userId=user_id)
        login.pop("discordVerifier", None)
        self.repo.put(login)
        return "Sign-in complete. Return to SGL. You can close this browser tab."

    def poll_auth(self, body):
        request_id, verifier = body.get("requestId", ""), body.get("verifier", "")
        require(isinstance(request_id, str) and re.fullmatch(r"[A-Za-z0-9_-]{43}", request_id), "Invalid sign-in request.")
        require(isinstance(verifier, str) and re.fullmatch(r"[A-Za-z0-9_-]{43,128}", verifier), "Invalid sign-in verifier.")
        login = self.repo.get("LOGIN#" + request_id)
        require(login and login["expiresAt"] > self.now(), "Sign-in expired. Start again.", 401)
        require(secrets.compare_digest(login["challenge"], challenge(verifier)), "Invalid sign-in verifier.", 401)
        require(login["status"] != "failed", login.get("failure", "Discord sign-in failed. Start again."), 502)
        if login["status"] != "ready":
            return {"status": "pending"}
        login = self.repo.consume_login("LOGIN#" + request_id)
        token = secrets.token_urlsafe(48)
        self.repo.put({"pk": "SESSION#" + digest(token.encode()), "sk": "META", "userId": login["userId"], "expiresAt": self.now() + 30 * 86_400}, absent=True)
        return {"status": "ready", "token": token, "expiresAt": self.now() + 30 * 86_400, "user": self.public_user(self.repo.get("USER#" + login["userId"]))}

    def upload(self, body, actor):
        sha, size = body.get("sha256", ""), body.get("size")
        require(isinstance(sha, str) and re.fullmatch(r"[a-f0-9]{64}", sha) and type(size) is int and 4 <= size <= MAX_MAP, "Invalid map checksum or size.")
        self.rate("upload", actor["id"], 20)
        upload_id = uuid.uuid4().hex
        key = "staging/" + actor["id"] + "/" + upload_id + ".bin"
        self.repo.put({"pk": "UPLOAD#" + upload_id, "sk": "META", "ownerId": actor["id"], "sha256": sha, "size": size, "key": key, "expiresAt": self.now() + 3600}, absent=True)
        return {"uploadId": upload_id, **self.repo.upload_url(key, size, sha), "expiresIn": 300}

    def blob(self, data, kind, extension, content_type):
        sha = digest(data)
        key = "blobs/" + kind + "/" + sha + extension
        self.repo.save_blob(key, data, content_type)
        return {"sha256": sha, "size": len(data), "key": key}

    def prepare_content(self, kind, content, actor, visibility, old=None):
        require(isinstance(content, dict), "Invalid item content.")
        if "reuseRevisionId" in content:
            require(kind == "map" and old is not None and old["ownerId"] == actor["id"] and set(content) == {"reuseRevisionId"}, "Only an owner can reuse a map revision from the same item.", 403)
            return self.revision(old, content["reuseRevisionId"])["content"].copy()
        if kind == "mode":
            return self.blob(mode(content.get("payload")), "mode", ".json", "application/json")
        if kind == "map":
            upload = self.repo.get("UPLOAD#" + identity(content.get("uploadId")))
            require(upload and upload["ownerId"] == actor["id"] and upload["expiresAt"] > self.now(), "Map upload expired or belongs to another account.", 403)
            data = self.repo.read_blob(upload["key"], MAX_MAP)
            require(len(data) == upload["size"] and digest(data) == upload["sha256"], "Uploaded map checksum or size did not match.")
            metadata, preview = map_archive(data)
            result = self.blob(data, "map", ".bin", "application/octet-stream")
            result["originalAuthor"] = str(metadata.get("author", ""))[:300]
            if preview:
                result["preview"] = self.blob(preview, "preview", ".jpg", "image/jpeg")
            return result
        require(kind == "bundle" and set(content) == {"map", "mode"}, "A bundle needs one map revision and one gamemode revision.")
        refs = {}
        for name in ("map", "mode"):
            ref = content[name]
            require(isinstance(ref, dict), "Invalid bundle reference.")
            item = self.item(ref.get("itemId"), actor)
            require(item["kind"] == name, "Bundle reference has the wrong content type.")
            self.revision(item, ref.get("revisionId"))
            require(visibility == "private" or item["visibility"] != "private", "Public or unlisted bundles cannot reference private content.")
            require(visibility != "public" or item["visibility"] == "public", "A public bundle must reference public items.")
            refs[name] = {"itemId": item["id"], "revisionId": ref["revisionId"]}
        return refs

    def publish(self, body, actor, item_id=None):
        request_id = identity(body.get("requestId"))
        validated_tags = tags(body["tags"]) if "tags" in body else None
        request_key = "REQUEST#" + actor["id"] + "#" + request_id
        request_hash = digest(json_bytes({"itemId": item_id, "body": body}))
        previous = self.repo.get(request_key)
        if previous:
            require(previous["requestHash"] == request_hash, "Publishing request identity was reused for different content.", 409)
            return {"itemId": previous["itemId"], "revisionId": previous["revisionId"], "shareUrl": self.base + "/share/" + previous["itemId"] + "?revision=" + previous["revisionId"]}
        self.rate("publish", actor["id"], 100)
        old = self.item(item_id, actor) if item_id else None
        if old:
            require(old["ownerId"] == actor["id"], "Make your own version to publish changes to another creator's item.", 403)
            require(body.get("expectedRevisionId") == old["latestRevisionId"], "A newer revision exists. Reload before publishing.", 409)
        kind = old["kind"] if old else body.get("kind")
        require(kind in ("map", "mode", "bundle"), "Choose a map, mode, or bundle.")
        visibility = body.get("visibility", old["visibility"] if old else "unlisted")
        require(visibility in ("public", "unlisted", "private"), "Invalid visibility.")
        title = text(body.get("title"), "title", 120)
        description = text(body.get("description", ""), "description", 2000, empty=True)
        note = text(body.get("changeNote", "Initial publication"), "change note", 1000)
        credits = text(body.get("credits", ""), "creator credits", 1000, empty=True)
        tag_values = validated_tags if "tags" in body else old.get("tags", []) if old else None
        source = body.get("source", old.get("source") if old else None)
        if source is not None:
            require(isinstance(source, str) and source.startswith("https://") and len(source) <= 1000 and not any(ord(c) < 32 for c in source), "Invalid source URL.")
        fork = old.get("forkOf") if old else body.get("forkOf")
        if fork and not old:
            require(isinstance(fork, dict), "Invalid original revision.")
            parent = self.item(fork.get("itemId"), actor)
            parent_revision = self.revision(parent, fork.get("revisionId"))
            require(parent["kind"] == kind, "Original item has a different content type.")
            fork = {"itemId": parent["id"], "revisionId": fork["revisionId"], "ownerId": parent["ownerId"], "credits": parent_revision.get("metadata", {}).get("credits", parent["credits"])}
            if tag_values is None:
                tag_values = parent_revision.get("metadata", {}).get("tags", [])
        # Validate explicit tags before accepting/uploading content. Missing tags
        # on older clients preserve the current revision's labels.
        if tag_values is not None:
            tag_values = tags(tag_values)
        content = self.prepare_content(kind, body.get("content"), actor, visibility, old)
        if tag_values is None and kind == "bundle":
            map_ref = content["map"]
            map_item = self.item(map_ref["itemId"], actor)
            tag_values = self.revision(map_item, map_ref["revisionId"]).get("metadata", {}).get("tags", [])
        tag_values = tags(tag_values if tag_values is not None else [])
        stamp, item_id, revision_id = self.stamp(), item_id or uuid.uuid4().hex, uuid.uuid4().hex
        item = {"pk": "ITEM#" + item_id, "sk": "META", "id": item_id, "kind": kind, "title": title, "description": description, "visibility": visibility, "credits": credits, "source": source, "forkOf": fork, "ownerId": actor["id"], "latestRevisionId": revision_id, "revisionNumber": old["revisionNumber"] + 1 if old else 1, "createdAt": old["createdAt"] if old else stamp, "updatedAt": stamp, "listPk": "PUBLIC" if visibility == "public" else "HIDDEN", "listSk": stamp + "#" + item_id, "ownerPk": "USER#" + actor["id"], "ownerSk": stamp + "#" + item_id}
        revision = {"pk": "ITEM#" + item_id, "sk": "REV#" + revision_id, "id": revision_id, "itemId": item_id, "number": item["revisionNumber"], "parentRevisionId": old["latestRevisionId"] if old else None, "actorId": actor["id"], "createdAt": stamp, "changeNote": note, "content": content, "listPk": "REVISIONS#" + item_id, "listSk": stamp + "#" + revision_id}
        item["tags"] = tag_values
        revision["metadata"] = {key: item[key] for key in ("title", "description", "credits", "source", "forkOf", "tags")}
        request = {"pk": request_key, "sk": "META", "itemId": item_id, "revisionId": revision_id, "requestHash": request_hash, "expiresAt": self.now() + 7 * 86_400}
        try:
            self.repo.publish(item, revision, old["latestRevisionId"] if old else None, request)
        except Problem:
            retry = self.repo.get(request_key)
            if retry and retry["requestHash"] == request_hash:
                return {"itemId": retry["itemId"], "revisionId": retry["revisionId"], "shareUrl": self.base + "/share/" + retry["itemId"] + "?revision=" + retry["revisionId"]}
            raise
        return {"itemId": item_id, "revisionId": revision_id, "shareUrl": self.base + "/share/" + item_id + "?revision=" + revision_id}

    def download(self, item, revision, actor):
        result = {"item": self.public_item(item), "revision": self.public_revision(revision)}
        result["item"].update(revision.get("metadata", {}))
        result["item"]["tags"] = revision.get("metadata", {}).get("tags", [])
        if item["kind"] == "bundle":
            result["dependencies"] = []
            for ref in revision["content"].values():
                dependency = self.item(ref["itemId"], actor)
                result["dependencies"].append(self.download(dependency, self.revision(dependency, ref["revisionId"]), actor))
            result["preview"] = next(d["preview"] for d in result["dependencies"] if d["item"]["kind"] == "map")
        else:
            result["preview"] = self.preview(item, revision, actor)
            blob = revision["content"]
            result["download"] = {"url": self.repo.download_url(blob["key"], "SGL-" + item["id"] + (".bin" if item["kind"] == "map" else ".json")), "sha256": blob["sha256"], "size": blob["size"]}
        return result

    def dispatch(self, method, path, query, headers, body, ip="unknown"):
        if path == "/health" and method == "GET":
            config = self.configuration()
            return {"service": "SGL sharing", "version": "1", "authConfigured": bool(config.get("clientId") and config.get("clientSecret")), "features": ["mapTags", "creatorFilter", "reuseMapRevision", "mapPreviews"]}
        if path == "/auth/start" and method == "POST": return self.start_auth(body, ip)
        if path == "/auth/poll" and method == "POST": return self.poll_auth(body)
        if path == "/auth/callback" and method == "GET": return self.callback(query)
        actor = self.actor(headers)
        if path == "/auth/logout" and method == "POST":
            self.actor(headers, required=True)
            self.repo.delete("SESSION#" + digest(headers["authorization"][7:].encode()))
            return {"signedOut": True}
        if path == "/me" and method == "GET": return self.public_user(self.actor(headers, required=True))
        if path in ("/items", "/me/items") and method == "GET":
            own = path == "/me/items"
            if own: actor = self.actor(headers, required=True)
            records, cursor = self.repo.listing("USER#" + actor["id"] if own else "PUBLIC", query.get("cursor"), owner=own)
            kind = query.get("kind")
            require(kind in (None, "map", "mode", "bundle"), "Invalid library filter.")
            tag = tags([query["tag"]])[0].casefold() if query.get("tag") else None
            creator = text(query["creator"], "creator filter", 100).casefold() if query.get("creator") else None
            # GSIs are eventually consistent. Recheck current visibility with a
            # strong base-table read before exposing any listing metadata.
            visible = []
            for record in records:
                current = self.repo.get("ITEM#" + record["id"])
                if current and (current["ownerId"] == actor["id"] if own else current["visibility"] == "public") and (not kind or current["kind"] == kind):
                    entry = self.public_item(current, actor)
                    owner = entry["owner"]
                    if tag and not any(label.casefold() == tag for label in entry["tags"]):
                        continue
                    if creator and not any(creator in str(value or "").casefold() for value in (entry["ownerId"], owner.get("displayName"), owner.get("discordId"))):
                        continue
                    entry["preview"] = self.preview(current, self.revision(current, current["latestRevisionId"]), actor) if current["kind"] in ("map", "bundle") else None
                    visible.append(entry)
            return {"items": visible, "cursor": cursor}
        if path == "/uploads" and method == "POST": return self.upload(body, self.actor(headers, required=True))
        if path == "/items" and method == "POST": return self.publish(body, self.actor(headers, required=True))
        match = re.fullmatch(r"/users/([a-f0-9]{32})", path)
        if match and method == "GET":
            user = self.repo.get("USER#" + match[1]); require(user, "Member not found.", 404)
            return self.public_user(user)
        match = re.fullmatch(r"/items/([a-f0-9]{32})(?:/revisions(?:/([a-f0-9]{32}))?)?", path)
        if match:
            item = self.item(match[1], actor)
            if method == "POST" and path.endswith("/revisions"):
                return self.publish(body, self.actor(headers, required=True), item["id"])
            if method == "GET":
                if match[2]: return self.download(item, self.revision(item, match[2]), actor)
                if path.endswith("/revisions"):
                    revisions, cursor = self.repo.listing("REVISIONS#" + item["id"], query.get("cursor"))
                    return {"revisions": [self.public_revision(r) for r in revisions], "cursor": cursor}
                return self.public_item(item, actor, include_preview=True)
        raise Problem(404, "Route not found.")

    def share_page(self, item_id, revision_id=None):
        if revision_id: identity(revision_id)
        try:
            item = self.item(item_id)
        except Problem as error:
            if error.status != 404: raise
            query = {"item": item_id}
            if revision_id: query["revision"] = revision_id
            link = html.escape("sgl://library/install?" + urllib.parse.urlencode(query), quote=True)
            # Same page for private and missing items: no existence or metadata leak.
            return f'<!doctype html><meta charset="utf-8"><title>SGL shared content</title><h1>Open this item in SGL</h1><p>Sign in there to check whether your account has access.</p><p><a href="{link}">Open in SGL</a></p><p><a href="https://sglifers.github.io/SGL/">Install SGL</a></p>'
        revision = self.revision(item, revision_id or item["latestRevisionId"])
        item = {**item, **revision.get("metadata", {})}
        item["tags"] = revision.get("metadata", {}).get("tags", [])
        owner = self.public_item(item)["owner"]["displayName"]
        title, description = html.escape(item["title"]), html.escape(item["description"] or revision["changeNote"])
        url = self.base + "/share/" + item_id + "?revision=" + revision["id"]
        revision_link = "sgl://library/install?" + urllib.parse.urlencode({"item": item_id, "revision": revision["id"]})
        preview = ""
        tag_html = "<p>Tags: " + " · ".join(html.escape(label) for label in item["tags"]) + "</p>" if item["tags"] else ""
        if item["kind"] == "map" and revision["content"].get("preview"):
            image = html.escape(self.repo.download_url(revision["content"]["preview"]["key"]), quote=True)
            preview = f'<meta property="og:image" content="{image}"><img src="{image}" alt="Map preview">'
        return f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{title} · SGL</title><meta property="og:title" content="{title}"><meta property="og:description" content="{description}"><meta property="og:url" content="{html.escape(url, quote=True)}"><style>body{{background:#10152b;color:#f5f7ff;font:17px system-ui;max-width:760px;margin:60px auto;padding:24px}}a{{color:#f3be4f}}img{{max-width:100%;border-radius:8px}}.button{{display:inline-block;background:#f3be4f;color:#171027;padding:12px 20px;border-radius:6px;text-decoration:none}}small{{color:#b9b7d6}}</style><h1>{title}</h1><p>{description}</p>{preview}{tag_html}<p>Published by {html.escape(owner)} · Revision {revision['number']}</p><p>Creator credits: {html.escape(item['credits'] or 'Not supplied')}</p><p><a class="button" href="{html.escape(revision_link, quote=True)}">Open in SGL</a></p><p>Or paste this link into SGL’s Shared library tab:</p><p><small>{html.escape(url)}</small></p><p><a href="https://sglifers.github.io/SGL/">Install SGL</a></p></html>'''


_service = None


def handler(event, context):
    global _service
    if _service is None:
        from repository import AwsRepository
        _service = Service(AwsRepository(), os.environ["PUBLIC_BASE_URL"])
    path = event.get("rawPath", "/")
    headers = {k.lower(): v for k, v in event.get("headers", {}).items()}
    query = event.get("queryStringParameters") or {}
    method = event.get("requestContext", {}).get("http", {}).get("method", "GET")
    response_headers = {"Content-Type": "application/json", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer"}
    try:
        raw = event.get("body") or "{}"
        if event.get("isBase64Encoded"): raw = base64.b64decode(raw).decode("utf-8")
        require(len(raw.encode()) <= 1_500_000, "Request body exceeds its limit.", 413)
        body = json.loads(raw)
        require(isinstance(body, dict), "Expected a JSON object.")
        share = re.fullmatch(r"/share/([a-f0-9]{32})", path)
        if method == "GET" and share:
            value = _service.share_page(share[1], query.get("revision"))
            response_headers["Content-Type"] = "text/html; charset=utf-8"
            response_headers["Content-Security-Policy"] = "default-src 'none'; img-src https:; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'"
            return {"statusCode": 200, "headers": response_headers, "body": value}
        result = _service.dispatch(method, path, query, headers, body, event.get("requestContext", {}).get("http", {}).get("sourceIp", "unknown"))
        if path == "/auth/callback":
            response_headers["Content-Type"] = "text/plain; charset=utf-8"
            return {"statusCode": 200, "headers": response_headers, "body": result}
        return {"statusCode": 200, "headers": response_headers, "body": json.dumps(result, ensure_ascii=False, allow_nan=False)}
    except Problem as error:
        return {"statusCode": error.status, "headers": response_headers, "body": json.dumps({"error": str(error)})}
    except (ValueError, TypeError, KeyError, UnicodeError):
        return {"statusCode": 400, "headers": response_headers, "body": '{"error":"Invalid request."}'}
    except Exception:
        # Never log authorization headers, bodies, OAuth codes or presigned URLs.
        print("SGL sharing request failed", getattr(context, "aws_request_id", "unknown"))
        return {"statusCode": 500, "headers": response_headers, "body": '{"error":"Sharing service could not complete this request. Try again."}'}
