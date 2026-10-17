// Runs only from the trusted checkout. PR objects are read through git, never checked out or executed.
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const git = (...args) => execFileSync('git', ['--literal-pathspecs', ...args], {
  encoding: 'utf8', maxBuffer: 12 * 1024 * 1024, timeout: 45000, stdio: ['ignore', 'pipe', 'pipe']
});
const supported = name => /\.(cs|csproj|props|targets|md|yml|yaml|js|cjs|ts|ps1|py|json)$/i.test(name);
const privateFile = name => /(^|\/)(\.env(?:\..*)?|.*\.(pem|key|pfx)|history\.json|facts\.json|.*memory.*\.json|context-state\.json)$/i.test(name);
const regularPaths = sha => new Set(git('ls-tree', '-r', '-z', sha).split('\0').filter(Boolean)
  .filter(entry => /^100(?:644|755) blob /.test(entry)).map(entry => entry.slice(entry.indexOf('\t') + 1)));

function excerpt(text, patch, removed) {
  const lines = text.split('\n');
  if (text.length <= 16000) return { content: lines.map((l, i) => `${i + 1}: ${l}`).join('\n'), truncated: false };
  const wanted = new Set(Array.from({ length: Math.min(50, lines.length) }, (_, i) => i));
  for (const match of patch.matchAll(/@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/g)) {
    const start = Number(match[removed ? 1 : 3]) - 1;
    const count = Number(match[removed ? 2 : 4] ?? 1);
    for (let i = Math.max(0, start - 20); i < Math.min(lines.length, start + count + 20); i++) wanted.add(i);
  }
  let result = '', previous = -2;
  for (const i of [...wanted].sort((a, b) => a - b)) {
    const line = (i !== previous + 1 ? '[...]\n' : '') + `${i + 1}: ${lines[i]}\n`;
    if (result.length + line.length > 24000) break;
    result += line; previous = i;
  }
  return { content: result, truncated: true };
}

async function prepare({ github, context, core }) {
  const number = Number(process.env.PR_NUMBER || context.payload.pull_request?.number);
  if (!Number.isSafeInteger(number) || number < 1) throw new Error('Invalid PR number');
  core.setOutput('pr_number', String(number));
  const { owner, repo } = context.repo;
  const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: number });
  if (pr.state !== 'open') throw new Error('PR is not open');
  const baseSha = pr.base.sha, headSha = pr.head.sha;
  if (![baseSha, headSha].every(s => /^[a-f0-9]{40}$/.test(s))) throw new Error('Invalid commit SHA');
  core.setOutput('head_sha', headSha);
  git('fetch', '--no-tags', 'origin', `refs/pull/${number}/head`);
  if (git('rev-parse', 'FETCH_HEAD').trim() !== headSha) throw new Error('PR changed during collection; rerun workflow');
  git('fetch', '--no-tags', 'origin', baseSha);
  const mergeBase = git('merge-base', baseSha, headSha).trim();
  const statuses = git('diff', '--no-renames', '--name-status', '-z', mergeBase, headSha).split('\0').filter(Boolean);
  if (statuses.length % 2 || statuses.length / 2 > 50) throw new Error('PR exceeds 50 changed files; split the PR');
  const basePaths = regularPaths(baseSha), headPaths = regularPaths(headSha), oldPaths = regularPaths(mergeBase);
  const coverage = [], files = [];
  let patchBudget = 120000;
  for (let i = 0; i < statuses.length; i += 2) {
    const status = statuses[i], name = statuses[i + 1];
    if (privateFile(name)) {
      files.push({ path: name, status, patch: '[sensitive file excluded]', content: '', truncated: true });
      coverage.push(`${name}: sensitive file excluded`); continue;
    }
    const patch = git('diff', '--no-ext-diff', '--no-textconv', '--no-renames', '--unified=8', mergeBase, headSha, '--', name);
    patchBudget -= patch.length;
    if (patchBudget < 0) throw new Error('PR diff exceeds 120000 characters; split the PR');
    const removed = status === 'D', sha = removed ? mergeBase : headSha;
    const paths = removed ? oldPaths : headPaths;
    let content = '', truncated = false;
    if (supported(name) && paths.has(name)) {
      const text = git('show', `${sha}:${name}`);
      if (!text.includes('\0')) ({ content, truncated } = excerpt(text, patch, removed));
      else { truncated = true; coverage.push(`${name}: binary content excluded`); }
    } else { truncated = true; coverage.push(`${name}: only diff available (unsupported type or symlink)`); }
    if (truncated && content) coverage.push(`${name}: bounded excerpts around changed lines`);
    files.push({ path: name, status, patch, content, truncated });
  }
  if (!files.length) throw new Error('PR has no changed files');
  const changedDirs = files.map(f => path.posix.dirname(f.path));
  const docs = [...basePaths].filter(n => n === 'README.md' || n.startsWith('docs/') && n.endsWith('.md')).sort();
  const code = [...basePaths].filter(n => n.endsWith('.cs') && !/^(tests|RevitAddin|tools)\//.test(n))
    .sort((a, b) => Number(changedDirs.includes(path.posix.dirname(b))) - Number(changedDirs.includes(path.posix.dirname(a))) || a.localeCompare(b));
  const corpus = [];
  for (const [names, limit, category] of [[docs, 150000, 'documentation'], [code, 300000, 'source']]) {
    let budget = limit, omitted = 0;
    for (const name of names) {
      if (privateFile(name)) { omitted++; continue; }
      if (budget <= 0) { omitted++; continue; }
      const text = git('show', `${baseSha}:${name}`);
      const size = Math.min(text.length, 60000, budget);
      corpus.push({ path: name, content: text.slice(0, size) }); budget -= size;
      if (size < text.length) { omitted++; coverage.push(`${name}: RAG source truncated to ${size} characters`); }
    }
    if (omitted) coverage.push(`RAG ${category}: ${omitted} files omitted or truncated by corpus budget`);
  }
  if (!corpus.some(f => f.path === 'README.md' || f.path.startsWith('docs/'))) throw new Error('README/docs missing from base branch');
  if (!corpus.some(f => f.path.endsWith('.cs'))) throw new Error('Source context missing from base branch');
  const output = process.env.REVIEW_INPUT;
  if (!output) throw new Error('REVIEW_INPUT is required');
  fs.writeFileSync(output, JSON.stringify({ repository: `${owner}/${repo}`, number, baseSha, headSha, files, context: corpus, coverage }));
  core.info(`Prepared ${files.length} changes and ${corpus.length} RAG source files for ${headSha}`);
}
module.exports = { prepare, excerpt, supported, privateFile };
