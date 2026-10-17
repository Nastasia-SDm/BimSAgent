const fs = require('node:fs');
const marker = '<!-- bimsagent-ai-review -->';
async function publish({ github, context, core }) {
  const number = Number(process.env.PR_NUMBER || context.payload.pull_request?.number);
  if (!Number.isSafeInteger(number) || number < 1) { core.warning('Cannot publish review: PR number missing'); return; }
  const { owner, repo } = context.repo;
  const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: number });
  const head = process.env.REVIEW_HEAD;
  if (pr.state !== 'open' || head && pr.head.sha !== head) { core.notice('Skipping obsolete review'); return; }
  const output = process.env.REVIEW_OUTPUT;
  let body = output && fs.existsSync(output) ? fs.readFileSync(output, 'utf8') :
    '## BimSAgent AI review — проверка не выполнена\n\nОшибка подготовки, сборки или timeout. Подробности: лог workflow. Это не результат review; PR не блокируется.';
  // Avoid mentions and image beacons; GitHub sanitizes remaining Markdown/HTML. Preserve C# generics and comparisons.
  body = body.replace(/<img\b[^>]*>/gi, '').replace(/!\[/g, '[').replace(/@/g, '@\u200b');
  const run = `${context.serverUrl}/${owner}/${repo}/actions/runs/${context.runId}`;
  body = `${marker}\n${body.slice(0, 50000)}\n\n[Лог workflow](${run})`;
  const comments = await github.paginate(github.rest.issues.listComments, { owner, repo, issue_number: number, per_page: 100 });
  const previous = comments.find(c => c.user?.type === 'Bot' && c.user?.login === 'github-actions[bot]' && c.body?.startsWith(marker));
  if (previous) await github.rest.issues.updateComment({ owner, repo, comment_id: previous.id, body });
  else await github.rest.issues.createComment({ owner, repo, issue_number: number, body });
  core.info(`Published AI review for PR #${number}`);
}
module.exports = { publish };
