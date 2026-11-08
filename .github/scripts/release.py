"""Gives a pull request merged into main the next version and publishes a GitHub release that also names the
latest version of the other repository at that moment.

Before 1.0.0 every pull request adds one to the last number. From 1.0.0 on, a feature (a title that starts with
feat, or a branch that starts with feat/) adds one to the middle number and sets the last one to 0, and anything
else, such as a fix, adds one to the last number. 1.0.0 itself, and any later major version, is tagged by hand.

The release workflow runs it on every push to main; a push that is not the merge of a pull request gets no
version. With --dry-run it only prints what it would publish, and needs no token.
"""
import argparse
import json
import os
import re
import subprocess
import urllib.request

TAG = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
FEATURE = re.compile(r"^feat(\([^)]*\))?!?:")


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def api(method, path, body=None):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "geowoof-release"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request("https://api.github.com" + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def latest(tags):
    versions = [tuple(int(part) for part in match.groups()) for match in map(TAG.match, tags) if match]
    return max(versions) if versions else None


def next_version(current, title, branch):
    major, minor, patch = current
    if major >= 1 and (FEATURE.match(title) or branch.startswith("feat/")):
        return major, minor + 1, 0
    return major, minor, patch + 1


def name(version):
    return "v" + ".".join(str(part) for part in version)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    parser.add_argument("--sha", default=os.environ.get("GITHUB_SHA"))
    parser.add_argument("--other-repo", required=True)
    parser.add_argument("--other-name", required=True)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    pulls = api("GET", f"/repos/{args.repo}/commits/{args.sha}/pulls")
    merged = [pull for pull in pulls if pull.get("merged_at") and pull["base"]["ref"] == "main"]
    if not merged:
        print("Not the merge of a pull request into main: no new version.")
        return
    pull = merged[0]

    if latest(git("tag", "--points-at", args.sha).split()):
        print("This commit already has a version.")
        return
    current = latest(git("tag", "--list", "v*").split()) or (0, 0, 0)
    version = name(next_version(current, pull["title"], pull["head"]["ref"]))

    remote = git("ls-remote", "--tags", "--refs", f"https://github.com/{args.other_repo}.git", "v*")
    other = latest(line.rsplit("refs/tags/", 1)[-1] for line in remote.splitlines())
    if other:
        other_text = f"[{name(other)}](https://github.com/{args.other_repo}/tree/{name(other)})"
    else:
        other_text = "ninguna todavía"
    notes = f"{pull['title']} (#{pull['number']})\n\n{args.other_name} más reciente al publicarla: {other_text}"

    if args.dry_run:
        print(f"Would publish {version} on {args.sha[:7]}:\n{notes}")
        return
    api("POST", f"/repos/{args.repo}/releases",
        {"tag_name": version, "target_commitish": args.sha, "name": version, "body": notes})
    print(f"Published {version}.")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as file:
            file.write(f"version={version.removeprefix('v')}\n")


if __name__ == "__main__":
    main()
