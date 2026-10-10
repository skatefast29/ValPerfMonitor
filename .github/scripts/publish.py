"""Upload a package zip to a Thunderstore-style API (Thunderstore and Hexium use the same one).

Usage: publish.py <api base url> <token env var> <zip> <author/team> <community categories JSON>
Steps: initiate upload -> PUT each part -> finish upload -> submit.
(Same sequence as Klastic/publish-to-hexium, MIT; written here so no third-party code sees the token.)
"""
import json
import os
import sys
import urllib.error
import urllib.request


def request(method, url, token=None, body=None, data=None):
    headers = {"User-Agent": "ValPerfMonitor-publish"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    elif data is not None:
        headers["Content-Type"] = "application/octet-stream"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as resp:
            return resp.headers, resp.read()
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {url.split('?')[0]} failed ({e.code}): {e.read().decode(errors='replace')[:500]}")


def api(url, token, body):
    _, raw = request("POST", url, token, body=body)
    return json.loads(raw) if raw else {}


def main():
    api_base, token_env, zip_path, author, categories = sys.argv[1:6]
    api_base = api_base.rstrip("/")
    token = os.environ.get(token_env, "")
    if not token:
        sys.exit(f"{token_env} is not set.")
    community_categories = json.loads(categories)
    archive = open(zip_path, "rb").read()

    init = api(f"{api_base}/usermedia/initiate-upload/", token,
               {"filename": os.path.basename(zip_path), "file_size_bytes": len(archive)})
    uuid = init["user_media"]["uuid"]

    try:
        parts = []
        for part in init["upload_urls"]:
            chunk = archive[part["offset"]:part["offset"] + part["length"]]
            headers, _ = request("PUT", part["url"], data=chunk)  # presigned URL: no token
            parts.append({"ETag": headers["ETag"], "PartNumber": part["part_number"]})
        api(f"{api_base}/usermedia/{uuid}/finish-upload/", token, {"parts": parts})
    except BaseException:
        try:
            request("POST", f"{api_base}/usermedia/{uuid}/abort-upload/", token, body={"uuid": uuid})
        except BaseException:
            pass  # keep the original error
        raise

    result = api(f"{api_base}/submission/submit/", token, {
        "author_name": author,
        "categories": [],
        "communities": list(community_categories),
        "community_categories": community_categories,
        "has_nsfw_content": False,
        "upload_uuid": uuid,
    })
    print(f"Published {os.path.basename(zip_path)} as {author} to {api_base}. hidden={result.get('hidden', False)}")


if __name__ == "__main__":
    main()
