import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";

test("Feature pushes preserve merged history but reject new foreign authorship", () => {
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

    try {
        git("init", "--quiet");
        const merged = run("git", ["commit", "--allow-empty", "-m", "Existing merged frontend"], {
            env: { ...owner, GIT_COMMITTER_NAME: "GitHub", GIT_COMMITTER_EMAIL: "noreply@github.com" }
        });
        assert.equal(merged.status, 0, merged.stderr);
        const base = git("rev-parse", "HEAD");
        git("commit", "--allow-empty", "-m", "New backend integration");
        const good = git("rev-parse", "HEAD");

        assert.equal(verify(good).status, 1);
        assert.equal(verify(good, base).status, 0);

        // New contributors cannot hide behind the base.
        const foreign = run("git", ["commit", "--allow-empty", "-m", "Foreign contribution"], {
            env: { ...owner, GIT_AUTHOR_NAME: "Other", GIT_AUTHOR_EMAIL: "other@example.test" }
        });
        assert.equal(foreign.status, 0, foreign.stderr);
        assert.equal(verify("HEAD", base).status, 1);

        // The hook only permits exclusions on feature pushes.
        git("update-ref", "refs/remotes/origin/clean-start", base);
        const hook = resolve(repository, ".githooks/pre-push");
        mkdirSync(resolve(directory, "scripts"));
        copyFileSync(resolve(repository, "scripts/verify-authorship.sh"), resolve(directory, "scripts/verify-authorship.sh"));
        const push = (sha, target, previous) => run(bash, [hook, "origin"], {
            input: `refs/heads/codex/test ${sha} refs/heads/${target} ${previous}\n`
        });
        const zero = "0".repeat(40);
        assert.equal(push(good, "codex/test", zero).status, 0);
        assert.equal(push(good, "clean-start", zero).status, 1);
        assert.equal(push(git("rev-parse", "HEAD"), "codex/test", good).status, 1);
    } finally {
        rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    }
});
