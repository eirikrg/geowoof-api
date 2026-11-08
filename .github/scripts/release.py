"""Publishes a version of what main has now: a tag vX.Y.Z and a GitHub release that lists the pull requests merged
since the previous version, and names the latest version of the other repository at that moment.

Merging into main does not make a version: several pull requests can go into one. The next number comes from
those pull requests. Before 1.0.0 it adds one to the last number. From 1.0.0 on, if one of them is a feature (a
title that starts with feat, or a branch that starts with feat/) it adds one to the middle number and sets the last
one to 0, and if not, as with fixes only, it adds one to the last number. 1.0.0 itself, and any later major
version, is chosen by hand with --version.

The release workflow runs it when someone presses its button in Actions. With --dry-run it only prints what it
would publish, and needs no token.
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


def parse(text):
    match = TAG.match(text)
    return tuple(int(part) for part in match.groups()) if match else None


def latest(tags):
    versions = [version for version in map(parse, tags) if version]
    return max(versions) if versions else None


def is_feature(change):
    return bool(FEATURE.match(change["title"])) or change["branch"].startswith("feat/")


def next_version(current, changes):
    major, minor, patch = current
    if major >= 1 and any(is_feature(change) for change in changes):
        return major, minor + 1, 0
    return major, minor, patch + 1


def name(version):
    return "v" + ".".join(str(part) for part in version)


def changes_since(repo, tag, sha):
    """What came into main after that tag, oldest first: each pull request once, and each direct push."""
    span = f"{name(tag)}..{sha}" if tag else sha
    changes, seen = [], set()
    for commit in git("rev-list", "--first-parent", "--reverse", span).split():
        pulls = [pull for pull in api("GET", f"/repos/{repo}/commits/{commit}/pulls")
                 if pull.get("merged_at") and pull["base"]["ref"] == "main"]
        if pulls:
            pull = pulls[0]
            if pull["number"] not in seen:
                seen.add(pull["number"])
                changes.append({"title": pull["title"], "branch": pull["head"]["ref"], "ref": f"#{pull['number']}"})
        else:
            subject = git("log", "-1", "--format=%s", commit).strip()
            changes.append({"title": subject, "branch": "", "ref": commit[:7]})
    return changes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    parser.add_argument("--sha", default=os.environ.get("GITHUB_SHA"))
    parser.add_argument("--other-repo", required=True)
    parser.add_argument("--other-name", required=True)
    parser.add_argument("--version", default="", help="Only to choose the number by hand, such as 1.0.0.")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if latest(git("tag", "--points-at", args.sha).split()):
        print("What main has now is already a version: there is nothing new to publish.")
        return
    current = latest(git("tag", "--list", "v*").split())
    changes = changes_since(args.repo, current, args.sha)
    if not changes:
        print("Nothing has come into main since the last version.")
        return

    chosen = args.version.strip().removeprefix("v")
    if chosen:
        version = parse("v" + chosen)
        if not version:
            raise SystemExit(f"{args.version} is not a version like 1.0.0.")
        if current and version <= current:
            raise SystemExit(f"{name(version)} is not after {name(current)}.")
    else:
        version = next_version(current or (0, 0, 0), changes)

    remote = git("ls-remote", "--tags", "--refs", f"https://github.com/{args.other_repo}.git", "v*")
    other = latest(line.rsplit("refs/tags/", 1)[-1] for line in remote.splitlines())
    if other:
        other_text = f"[{name(other)}](https://github.com/{args.other_repo}/tree/{name(other)})"
    else:
        other_text = "ninguna todavía"
    listed = "\n".join(f"- {change['title']} ({change['ref']})" for change in changes)
    notes = f"{listed}\n\n{args.other_name} más reciente al publicarla: {other_text}"

    if args.dry_run:
        print(f"Would publish {name(version)} on {args.sha[:7]}:\n{notes}")
        return
    api("POST", f"/repos/{args.repo}/releases",
        {"tag_name": name(version), "target_commitish": args.sha, "name": name(version), "body": notes})
    print(f"Published {name(version)}.")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as file:
            file.write(f"version={name(version).removeprefix('v')}\n")


if __name__ == "__main__":
    main()
