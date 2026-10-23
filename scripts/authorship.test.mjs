import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { resolve, sep } from "node:path";
import { test } from "node:test";

test("Feature and PR checks preserve merged history but reject new foreign authorship", () => {
    const repository = resolve(import.meta.dirname, "..");
    const scratch = resolve(repository, ".scratch");
    mkdirSync(scratch, { recursive: true });
    const directory = mkdtempSync(resolve(scratch, "authorship-"));
    const bash = process.platform === "win32" ? resolve(process.env.ProgramFiles, "Git/bin/bash.exe") : "bash";
    const owner = {
        ...process.env,
        GIT_AUTHOR_NAME: "Juloc", GIT_AUTHOR_EMAIL: "juli.hiresch@gmail.com",
        GIT_COMMITTER_NAME: "Juloc", GIT_COMMITTER_EMAIL: "juli.hiresch@gmail.com"
    };
    const run = (command, args, options = {}) => spawnSync(command, args, { cwd: directory, env: owner, encoding: "utf8", ...options });
    const git = (...args) => {
        const result = run("git", args);
        assert.equal(result.status, 0, result.stderr);
        return result.stdout.trim();
    };
    const verify = (range, base) => run(bash, [resolve(repository, "scripts/verify-authorship.sh"), "--range", range, ...(base ? ["--exclude", base] : [])]);
    const workflow = readFileSync(resolve(repository, ".github/workflows/authorship.yml"), "utf8").replaceAll("\r\n", "\n");
    const workflowScript = workflow.match(/        run: \|\n((?:          .*\n?)*)/)[1].replace(/^          /gm, "");
    const checkPullRequest = (head, eventBase, target) => run(bash, ["-c", workflowScript], {
        env: { ...owner, EVENT_NAME: "pull_request", BASE_SHA: eventBase, BASE_REF: target, HEAD_SHA: head }
    });

    try {
        git("init", "--quiet");
        git("commit", "--allow-empty", "-m", "Original baseline");
        const original = git("rev-parse", "HEAD");
        const merged = run("git", ["commit", "--allow-empty", "-m", "Existing merged frontend"], {
            env: { ...owner, GIT_AUTHOR_NAME: "Other", GIT_AUTHOR_EMAIL: "other@example.test", GIT_COMMITTER_NAME: "Other", GIT_COMMITTER_EMAIL: "other@example.test" }
        });
        assert.equal(merged.status, 0, merged.stderr);
        const base = git("rev-parse", "HEAD");
        git("commit", "--allow-empty", "-m", "New backend integration");
        const good = git("rev-parse", "HEAD");

        // GitHub itself is the committer of a squash or merge commit made on github.com; its author is still checked.
        const web = run("git", ["commit", "--allow-empty", "-m", "Web squash merge"], {
            env: { ...owner, GIT_COMMITTER_NAME: "GitHub", GIT_COMMITTER_EMAIL: "noreply@github.com" }
        });
        assert.equal(web.status, 0, web.stderr);
        assert.equal(verify("HEAD", good).status, 0);
        const foreignWeb = run("git", ["commit", "--allow-empty", "-m", "Web merge by somebody else"], {
            env: { ...owner, GIT_AUTHOR_NAME: "Other", GIT_AUTHOR_EMAIL: "other@example.test", GIT_COMMITTER_NAME: "GitHub", GIT_COMMITTER_EMAIL: "noreply@github.com" }
        });
        assert.equal(foreignWeb.status, 0, foreignWeb.stderr);
        assert.equal(verify("HEAD", good).status, 1);
        git("reset", "--hard", good);

        // Run the actual CI script with a stale event and the current fetched target.
        mkdirSync(resolve(directory, "scripts"));
        copyFileSync(resolve(repository, "scripts/verify-authorship.sh"), resolve(directory, "scripts/verify-authorship.sh"));
        git("update-ref", "refs/remotes/origin/clean-start", base);
        assert.equal(checkPullRequest(good, original, "clean-start").status, 0);
        git("update-ref", "refs/remotes/origin/main", original);
        assert.equal(checkPullRequest(good, original, "main").status, 1);

        assert.equal(verify(good).status, 1);
        assert.equal(verify(good, base).status, 0);

        // New contributors cannot hide behind the base.
        const foreign = run("git", ["commit", "--allow-empty", "-m", "Foreign contribution"], {
            env: { ...owner, GIT_AUTHOR_NAME: "Other", GIT_AUTHOR_EMAIL: "other@example.test" }
        });
        assert.equal(foreign.status, 0, foreign.stderr);
        assert.equal(verify("HEAD", base).status, 1);
        assert.equal(checkPullRequest(git("rev-parse", "HEAD"), original, "clean-start").status, 1);

        // The hook only permits exclusions on feature pushes.
        git("update-ref", "refs/remotes/origin/main", base);
        const hook = resolve(repository, ".githooks/pre-push");
        const push = (sha, target, previous) => run(bash, [hook, "origin"], {
            input: `refs/heads/codex/test ${sha} refs/heads/${target} ${previous}\n`
        });
        const zero = "0".repeat(40);
        assert.equal(push(good, "codex/test", zero).status, 0);
        assert.equal(push(good, "fix/test", zero).status, 0);
        assert.equal(push(good, "fix/test", original).status, 0);
        for (const target of ["dev", "main"]) {
            assert.equal(push(good, target, original).status, 1);
        }
        assert.equal(push(git("rev-parse", "HEAD"), "codex/test", good).status, 1);
        assert.equal(push(git("rev-parse", "HEAD"), "fix/test", good).status, 1);
    } finally {
        assert.ok(resolve(directory).startsWith(scratch + sep));
        rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    }
});
