const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { prepare, excerpt, privateFile } = require('./prepare-review.cjs');
const { publish } = require('./publish-review.cjs');

test('large changed files include deep changed lines and accurate line numbers', () => {
  const text = Array.from({ length: 3000 }, (_, i) => `line ${i + 1}: code payload`).join('\n');
  const result = excerpt(text, '@@ -2500,2 +2500,2 @@', false);
  assert.equal(result.truncated, true);
  assert.match(result.content, /2500: line 2500/);
  assert.equal(privateFile('.env'), true);
  assert.equal(privateFile('history.json'), true);
  assert.equal(privateFile('Program.cs'), false);
});

test('collect actual Git PR objects as data, including additions and removals', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bims-git-review-'));
  const previous = process.cwd(), oldInput = process.env.REVIEW_INPUT, oldNumber = process.env.PR_NUMBER;
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  try {
    git('init'); git('config', 'user.name', 'Review Test'); git('config', 'user.email', 'review@example.invalid');
    fs.writeFileSync(path.join(root, 'README.md'), '# BimSAgent\nReview documentation');
    fs.writeFileSync(path.join(root, 'Program.cs'), 'old source\n');
    fs.writeFileSync(path.join(root, 'Removed.cs'), 'removed source\n');
    git('add', '.'); git('commit', '-m', 'base'); const base = git('rev-parse', 'HEAD');
    fs.writeFileSync(path.join(root, 'Program.cs'), 'new source\n');
    fs.unlinkSync(path.join(root, 'Removed.cs'));
    fs.writeFileSync(path.join(root, 'Added.cs'), 'added source\n');
    git('add', '.'); git('commit', '-m', 'head'); const head = git('rev-parse', 'HEAD');
    git('update-ref', 'refs/pull/1/head', head); git('remote', 'add', 'origin', root);
    process.chdir(root); process.env.PR_NUMBER = '1'; process.env.REVIEW_INPUT = path.join(root, 'input.json');
    const outputs = {};
    await prepare({ github: { rest: { pulls: { get: async () => ({ data: { state: 'open', base: { sha: base }, head: { sha: head } } }) } } },
      context: { repo: { owner: 'owner', repo: 'repo' }, payload: {} }, core: { setOutput: (k, v) => outputs[k] = v, info() {} } });
    const snapshot = JSON.parse(fs.readFileSync(process.env.REVIEW_INPUT));
    assert.equal(outputs.head_sha, head);
    assert.match(snapshot.files.find(f => f.path === 'Program.cs').patch, /\+new source/);
    assert.equal(snapshot.files.find(f => f.path === 'Added.cs').status, 'A');
    assert.match(snapshot.files.find(f => f.path === 'Removed.cs').content, /removed source/);
    assert.match(snapshot.context.find(f => f.path === 'Program.cs').content, /old source/);
    assert.equal(git('rev-parse', 'HEAD'), head); // No checkout or modifications by collector.
  } finally {
    process.chdir(previous);
    if (oldInput === undefined) delete process.env.REVIEW_INPUT; else process.env.REVIEW_INPUT = oldInput;
    if (oldNumber === undefined) delete process.env.PR_NUMBER; else process.env.PR_NUMBER = oldNumber;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('publisher updates bot comment, suppresses mentions/images and skips stale SHA', async () => {
  const old = { ...process.env };
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bims-publish-review-'));
  try {
    process.env.PR_NUMBER = '1'; process.env.REVIEW_HEAD = 'current'; process.env.REVIEW_OUTPUT = path.join(root, 'review.md');
    fs.writeFileSync(process.env.REVIEW_OUTPUT, 'review @someone ![image](https://example.invalid) <img src="x"> `Task<T>` and `a < b && c > d`');
    const changes = [];
    const github = { rest: { pulls: { get: async () => ({ data: { state: 'open', head: { sha: 'current' } } }) }, issues: {
      listComments() {}, updateComment: async body => changes.push(body), createComment: async () => assert.fail('duplicate comment')
    } }, paginate: async () => [{ id: 12, user: { type: 'Bot', login: 'github-actions[bot]' }, body: '<!-- bimsagent-ai-review --> previous' }] };
    const args = { github, context: { repo: { owner: 'owner', repo: 'repo' }, payload: {}, runId: 1, serverUrl: 'https://github.com' },
      core: { info() {}, notice() {}, warning() {} } };
    await publish(args);
    assert.equal(changes[0].comment_id, 12);
    assert.ok(!changes[0].body.includes('@someone')); assert.ok(!changes[0].body.includes('![')); assert.ok(!changes[0].body.includes('<img')); assert.ok(changes[0].body.includes('Task<T>')); assert.ok(changes[0].body.includes('a < b && c > d'));
    process.env.REVIEW_HEAD = 'obsolete'; await publish(args); assert.equal(changes.length, 1);
  } finally { process.env = old; fs.rmSync(root, { recursive: true, force: true }); }
});

test('publisher creates first diagnostic comment when review output is missing', async () => {
  const old = { ...process.env };
  try {
    process.env.PR_NUMBER = '1'; process.env.REVIEW_HEAD = 'current'; delete process.env.REVIEW_OUTPUT;
    let created;
    const args = { github: { rest: {
      pulls: { get: async () => ({ data: { state: 'open', head: { sha: 'current' } } }) },
      issues: { listComments() {}, createComment: async value => created = value }
    }, paginate: async () => [] },
    context: { repo: { owner: 'owner', repo: 'repo' }, payload: {}, runId: 1, serverUrl: 'https://github.com' },
    core: { info() {}, notice() {}, warning() {} } };
    await publish(args);
    assert.equal(created.issue_number, 1);
    assert.match(created.body, /проверка не выполнена/);
    assert.match(created.body, /PR не блокируется/);
  } finally { process.env = old; }
});