// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Port do load_test/run-50k.js do repo teams_msgs (TS) para esta stack:
//   .NET 8 + AKS + Storage Queue + Table Storage (Workload Identity)
//
// 1. Seed N fake refs no Table Storage 'conversationrefs' (clonando 1 ref real)
//    — rowKey segue o mesmo formato que ConversationRefStore.ToSafeRowKey:
//      base64url(conversationId) com a convenção fake-<i>-<ts>
// 2. POST /api/send (cria 1 job → N mensagens enfileiradas no Storage Queue)
// 3. Poll progresso até completar
// 4. Cleanup das fake refs
//
// Variáveis de ambiente:
//   BOT_URL                FQDN da API (default https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com)
//   API_KEY                x-api-key da API
//   STORAGE_ACCOUNT        nome da Storage Account (ex.: sttmdpocr4mzki)
//   STORAGE_CONNECTION     OU connection string completa
//
// Uso (Windows PowerShell):
//   $env:BOT_URL="https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com"
//   $env:API_KEY="<sua-api-key>"
//   $env:STORAGE_ACCOUNT="sttmdpocr4mzki"   # usa az CLI login (DefaultAzureCredential)
//   node load_test/run-50k.js --refs 1000

const { TableClient, AzureNamedKeyCredential } = require("@azure/data-tables");
const { DefaultAzureCredential } = require("@azure/identity");

const BASE_URL = process.env.BOT_URL || "https://teams-msgs-dotnet.brazilsouth.cloudapp.azure.com";
const API_KEY = process.env.API_KEY || "";
const STORAGE_ACCOUNT = process.env.STORAGE_ACCOUNT || "";
const STORAGE_CONNECTION = process.env.STORAGE_CONNECTION || "";
const POLL_INTERVAL_MS = 2000;
const TIMEOUT_MS = 60 * 60 * 1000;

const args = process.argv.slice(2);
const getArg = (name, dflt) => {
  const idx = args.indexOf(`--${name}`);
  return idx >= 0 && args[idx + 1] ? parseInt(args[idx + 1], 10) : dflt;
};
const hasFlag = (name) => args.includes(`--${name}`);

const TARGET_REFS = getArg("refs", 1000);
const SKIP_SEED = hasFlag("skip-seed");
const CLEANUP_ONLY = hasFlag("cleanup");

function authHeaders() {
  const h = { "Content-Type": "application/json" };
  if (API_KEY) h["x-api-key"] = API_KEY;
  return h;
}

function safeRowKey(conversationId) {
  return Buffer.from(conversationId, "utf-8")
    .toString("base64")
    .replace(/=+$/, "")
    .replace(/\+/g, "-")
    .replace(/\//g, "_");
}

function getTableClient() {
  if (STORAGE_CONNECTION) {
    return TableClient.fromConnectionString(STORAGE_CONNECTION, "conversationrefs");
  }
  if (!STORAGE_ACCOUNT) {
    throw new Error("Defina STORAGE_ACCOUNT ou STORAGE_CONNECTION");
  }
  const url = `https://${STORAGE_ACCOUNT}.table.core.windows.net`;
  return new TableClient(url, "conversationrefs", new DefaultAzureCredential());
}

async function seedRefs(count) {
  console.log(`\n🌱 Seeding ${count} fake refs no Table Storage…\n`);
  const table = getTableClient();
  try { await table.createTable(); } catch {}

  const realRefs = [];
  const iter = table.listEntities({ queryOptions: { filter: "PartitionKey eq 'refs'" } });
  for await (const e of iter) {
    if (!e.rowKey.startsWith(safeRowKey("fake-"))) {
      realRefs.push(e);
    }
  }
  if (realRefs.length === 0) {
    console.error("❌ Nenhuma ref real encontrada. Instale o Teams app primeiro.");
    process.exit(1);
  }
  console.log(`  Refs reais encontradas: ${realRefs.length}`);

  const BATCH = 50;
  let created = 0;
  const start = Date.now();
  for (let i = 0; i < count; i += BATCH) {
    const promises = [];
    for (let j = i; j < Math.min(i + BATCH, count); j++) {
      const base = realRefs[j % realRefs.length];
      const fakeId = `fake-${j}-${Date.now()}`;
      const fakeRowKey = safeRowKey(fakeId);
      const fakeRef = JSON.parse(base.refJson);
      fakeRef.conversation = { ...fakeRef.conversation, id: fakeId };
      promises.push(
        table.upsertEntity(
          { partitionKey: "refs", rowKey: fakeRowKey, refJson: JSON.stringify(fakeRef) },
          "Replace"
        )
      );
    }
    await Promise.all(promises);
    created += promises.length;
    if (created % 200 === 0 || created === count) {
      const elapsed = ((Date.now() - start) / 1000).toFixed(0);
      const rate = (created / ((Date.now() - start) / 1000)).toFixed(0);
      process.stdout.write(`\r  ✅ ${created}/${count} seeded (${rate}/s, ${elapsed}s)`);
    }
  }
  console.log("\n");
  return created;
}

async function cleanupRefs() {
  console.log("\n🧹 Cleaning up fake refs…\n");
  const table = getTableClient();
  const fakePrefix = safeRowKey("fake-").slice(0, 5);
  const fakeRefs = [];
  const iter = table.listEntities({ queryOptions: { filter: "PartitionKey eq 'refs'" } });
  for await (const e of iter) {
    // Heurística: a ref real do Bot Framework não começa com Buffer.from('fake-')...
    if (e.rowKey.startsWith(fakePrefix)) {
      fakeRefs.push(e);
    }
  }
  const total = fakeRefs.length;
  console.log(`  ${total} fake refs encontradas`);
  const BATCH = 50;
  let deleted = 0;
  for (let i = 0; i < fakeRefs.length; i += BATCH) {
    const promises = fakeRefs.slice(i, i + BATCH).map((r) =>
      table.deleteEntity("refs", r.rowKey).catch(() => {})
    );
    await Promise.all(promises);
    deleted += promises.length;
    if (deleted % 200 === 0 || deleted === total) {
      process.stdout.write(`\r  🗑️  ${deleted}/${total} deleted`);
    }
  }
  console.log("\n\nDone.\n");
  return deleted;
}

async function pollJob(jobId) {
  const r = await fetch(`${BASE_URL}/api/jobs/${jobId}`, { headers: authHeaders() });
  return await r.json();
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  if (CLEANUP_ONLY) { await cleanupRefs(); return; }

  console.log("=".repeat(64));
  console.log("  LOAD TEST — .NET 8 + AKS + Storage Queue + Workload Identity");
  console.log("=".repeat(64));
  console.log(`  URL:         ${BASE_URL}`);
  console.log(`  Target:      ${TARGET_REFS} refs`);
  console.log(`  Skip seed:   ${SKIP_SEED}`);
  console.log(`  Auth:        ${API_KEY ? "x-api-key set" : "DISABLED"}`);
  console.log("=".repeat(64));

  if (!SKIP_SEED) await seedRefs(TARGET_REFS);

  const s = await (await fetch(`${BASE_URL}/api/status`, { headers: authHeaders() })).json();
  console.log(`📋 Refs registradas: ${s.registeredUsers}`);

  console.log(`\n🚀 Enviando 1 job para ${s.registeredUsers} usuários…\n`);
  const sendStart = Date.now();
  const sendResp = await fetch(`${BASE_URL}/api/send`, {
    method: "POST",
    headers: authHeaders(),
    body: JSON.stringify({ message: `📊 Load test ${TARGET_REFS} — ${new Date().toISOString()}` }),
  });
  if (!sendResp.ok) {
    console.error(`❌ ${sendResp.status} ${await sendResp.text()}`);
    process.exit(1);
  }
  const job = await sendResp.json();
  const enqueueTime = Date.now() - sendStart;
  console.log(`📬 Job ${job.jobId} | total=${job.total} | enqueue=${(enqueueTime/1000).toFixed(1)}s\n`);

  const pollStart = Date.now();
  let lastProgress = -1;
  while (Date.now() - pollStart < TIMEOUT_MS) {
    await sleep(POLL_INTERVAL_MS);
    const j = await pollJob(job.jobId).catch(() => null);
    if (!j) continue;
    const processed = (j.sent || 0) + (j.failed || 0);
    const elapsed = ((Date.now() - pollStart) / 1000).toFixed(0);
    const rate = processed > 0 ? ((processed / (Date.now() - pollStart)) * 60000).toFixed(0) : "0";
    if (j.progress !== lastProgress) {
      process.stdout.write(`\r  ${j.progress}% | ${processed}/${j.total} | sent=${j.sent} failed=${j.failed} | ${rate} msg/min | ${elapsed}s   `);
      lastProgress = j.progress;
    }
    if (j.status === "completed") {
      const totalTime = Date.now() - sendStart;
      console.log("\n\n" + "=".repeat(64));
      console.log("  RESULTADO");
      console.log("=".repeat(64));
      console.log(`  Total:         ${j.total}`);
      console.log(`  Sent:          ${j.sent}`);
      console.log(`  Failed:        ${j.failed}`);
      console.log(`  Enqueue:       ${(enqueueTime/1000).toFixed(1)}s`);
      console.log(`  Processing:    ${((Date.now()-pollStart)/1000).toFixed(1)}s`);
      console.log(`  Total:         ${(totalTime/1000).toFixed(1)}s`);
      console.log(`  Throughput:    ${((processed/(Date.now()-pollStart))*60000).toFixed(0)} msg/min`);
      console.log("=".repeat(64));
      if (j.errors?.length) {
        const uniq = [...new Set(j.errors)];
        console.log(`\n  Erros únicos (${uniq.length}): ${uniq.slice(0,3).join(" | ")}`);
      }
      const fs = require("fs");
      fs.writeFileSync(__dirname + "/report.json", JSON.stringify({
        timestamp: new Date().toISOString(),
        stack: ".NET 8 + AKS + KEDA + Storage Queue + Workload Identity",
        config: { targetRefs: TARGET_REFS, actualRefs: j.total },
        results: {
          sent: j.sent, failed: j.failed,
          enqueueMs: enqueueTime,
          processingMs: Date.now() - pollStart,
          totalMs: totalTime,
          throughputPerMin: parseFloat(((processed/(Date.now()-pollStart))*60000).toFixed(2)),
        },
      }, null, 2));
      console.log("\n📄 Relatório em load_test/report.json\n");
      await cleanupRefs();
      return;
    }
  }
  console.log("\n❌ Timeout!");
  await cleanupRefs();
}

main().catch((e) => { console.error(e); process.exit(1); });
