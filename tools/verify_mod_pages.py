"""Check prepared project-page availability without changing its author mapping.

HTTP 401/403/429 and network failures are inconclusive, not proof that an author
removed a page. The report never marks mapping entries unavailable automatically.
Project/author identity must also be checked against the official pack provenance.
"""
import argparse
import concurrent.futures
import datetime
import hashlib
import html
import json
import pathlib
import re
import urllib.error
import urllib.request
from urllib.parse import urlsplit


def check_page(url):
    parsed = urlsplit(url)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        return {"url": url, "state": "invalid"}
    request = urllib.request.Request(url, headers={"User-Agent": "LANMinecraft-ProjectPageVerification/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            payload = response.read(768 * 1024).decode("utf-8", errors="replace")
            title = re.search(r"<title[^>]*>(.*?)</title>", payload, flags=re.I | re.S)
            return {"url": url, "state": "http_ok", "httpStatus": response.status,
                    "resolvedUrl": response.url,
                    "title": html.unescape(re.sub(r"\s+", " ", title[1])).strip()[:300] if title else None}
    except urllib.error.HTTPError as error:
        return {"url": url, "httpStatus": error.code,
                "state": "http_missing" if error.code in (404, 410) else "inconclusive"}
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        return {"url": url, "state": "inconclusive", "errorType": type(error).__name__}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pages", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--workers", default=12, type=int)
    args = parser.parse_args()
    payload = args.pages.read_bytes()
    mapping = json.loads(payload.decode("utf-8-sig"))
    urls = sorted({value["url"] if isinstance(value, dict) else value for value in mapping.values()})
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, min(args.workers, 16))) as executor:
        results = list(executor.map(check_page, urls))
    counts = {state: sum(row["state"] == state for row in results)
              for state in sorted({row["state"] for row in results})}
    report = {"schemaVersion": 1, "checkedAtUtc": datetime.datetime.now(datetime.UTC).isoformat(),
              "mappingSha256": hashlib.sha256(payload).hexdigest(), "counts": counts, "pages": results}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"entries": len(mapping), "uniqueUrls": len(urls), "counts": counts}))


if __name__ == "__main__":
    main()
