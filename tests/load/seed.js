#!/usr/bin/env node
//
// Deep Link Engine — load-profile corpus seeder.
//
// Creates the links resolve.js needs and writes corpus.json next to it. Node rather than k6, for one
// reason: k6 has no way to write a file, and the corpus has to outlive the process that created it
// so that a run can be repeated against the same links. Node 22 already builds the admin console and
// runs this seeder in release.yml, so this adds nothing to the tool chain.
//
//   node seed.js \
//     --control https://control.example.com \
//     --api-key dle_<prefix>_<secret> \
//     --domain-id 5d7c9e2b-4f16-4a8e-9c2d-8f4c1f0a77b1 \
//     --host link.example.com \
//     --total 10000 --hot 100
//
// The four required values can come from the environment instead of the flags: DLE_CONTROL_URL,
// DLE_API_KEY, DLE_DOMAIN_ID and DLE_LINK_HOST.
//
// The counts default to the profile's own needs: a hundred hot links, because the hot set
// is the top hundred, and ten thousand in total so that the 15 % "random existing" stream is spread
// widely enough to actually miss the cache sometimes. A corpus of a few hundred would sit entirely
// in L1 and the run would report a cache hit rate of 100 % that means nothing.
//
// It needs a deployed control plane, an API key with the links:write scope and an active link
// domain (its id and its host; a domain is active on registration unless the instance requires
// ownership verification); release.yml runs it before the k6 gate. Seeding goes
// through the bulk endpoint by default (--bulk false creates the links one at a time, retrying on
// 429), and every slug starts with the --prefix (default k6load), so the links can be found and
// deleted afterwards.

import { writeFile } from 'node:fs/promises';
import { argv, exit } from 'node:process';

const args = parseArgs(argv.slice(2));

const CONTROL = required('control', 'DLE_CONTROL_URL').replace(/\/+$/, '');
const API_KEY = required('api-key', 'DLE_API_KEY');
const DOMAIN_ID = required('domain-id', 'DLE_DOMAIN_ID');
const HOST = required('host', 'DLE_LINK_HOST');

const TOTAL = Number(args['total'] ?? 10000);
const HOT = Number(args['hot'] ?? 100);
const CONCURRENCY = Number(args['concurrency'] ?? 16);
const USE_BULK = args['bulk'] !== 'false' && args['bulk'] !== false;
const OUTPUT = args['out'] ?? new URL('./corpus.json', import.meta.url).pathname;

if (!Number.isInteger(TOTAL) || TOTAL < HOT || HOT < 1) {
  fail(`--total must be an integer of at least --hot (${HOT}); got ${TOTAL}.`);
}

// A stable prefix, so a second run against the same instance replaces the same corpus rather than
// doubling it, and so an operator can find and delete these links afterwards with one query.
const PREFIX = args['prefix'] ?? 'k6load';

/**
 * Builds one link's creation request.
 *
 * The rule set is deliberately non-trivial: a platform-scoped rule and a default, which is the
 * shape a real campaign has. A corpus of links whose rule set is a
 * single default would measure the evaluator's fastest path and nothing else.
 */
function linkRequest(index) {
  const slug = `${PREFIX}${index.toString(36).padStart(5, '0')}`;

  return {
    slug,
    domain_id: DOMAIN_ID,
    title: `Load profile link ${index}`,
    target_url: `https://www.example.com/promo/${index % 50}`,
    deeplink_path: `/promo/${index % 50}`,
    utm: {
      utm_source: 'k6',
      utm_medium: 'load',
      utm_campaign: `profile-${index % 10}`,
    },
    routing_rules: [
      {
        id: 'ios',
        when: { platform: ['ios'] },
        then: {
          action: 'app_or_store',
          deeplink_path: `/promo/${index % 50}`,
          store_url: 'https://apps.apple.com/app/id123456789?pt=1234&ct=k6&mt=8',
          interstitial: 'auto',
        },
      },
      {
        id: 'android',
        when: { platform: ['android'] },
        then: {
          action: 'app_or_store',
          deeplink_path: `/promo/${index % 50}`,
          store_url: 'https://play.google.com/store/apps/details?id=com.example',
          referrer_template: 'dl_cid={click_id}&utm_source={utm_source}&utm_campaign={utm_campaign}',
        },
      },
      {
        id: 'default',
        then: { action: 'web', url: `https://www.example.com/promo/${index % 50}` },
      },
    ],
  };
}

async function createLink(index) {
  const body = linkRequest(index);

  for (let attempt = 0; attempt < 5; attempt++) {
    const response = await fetch(`${CONTROL}/api/v1/links`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${API_KEY}`,
        'Content-Type': 'application/json',
        // The same key with the same body replays the stored response, so re-running the seeder
        // against a partially seeded instance is idempotent rather than a wall of 409s.
        'Idempotency-Key': `${PREFIX}-${index}`,
      },
      body: JSON.stringify(body),
    });

    if (response.status === 201 || response.status === 200 || response.status === 409) {
      return body.slug;
    }

    if (response.status === 429) {
      const delay = Math.pow(2, attempt) * 1000 + Math.random() * 500;
      await new Promise((r) => setTimeout(r, delay));
      continue;
    }

    const detail = await response.text();
    throw new Error(
      `Creating ${body.slug} failed with ${response.status}: ${detail.slice(0, 400)}`,
    );
  }

  throw new Error(`Creating ${body.slug} failed: rate limited after 5 attempts.`);
}

// The bulk endpoint replays a repeated Idempotency-Key with its stored summary, which has no rows to
// build a corpus from. Each run therefore gets its own keys; links an earlier run created come back
// as slug-taken rows, which name a usable slug.
const RUN = Date.now().toString(36);

/** Seeds links via bulk NDJSON endpoint (POST /api/v1/links/bulk). */
async function seedBulk() {
  const slugs = [];
  const BATCH_SIZE = Math.min(TOTAL, 2000);

  for (let start = 0; start < TOTAL; start += BATCH_SIZE) {
    const end = Math.min(start + BATCH_SIZE, TOTAL);
    let ndjson = '';
    // The slug this client asked for, by row ref. The corpus is built from these alone, never from
    // text the server sent back, so nothing the network says ends up in the file verbatim.
    const requested = new Map();

    for (let i = start; i < end; i++) {
      const req = linkRequest(i);
      requested.set(String(i), req.slug);
      ndjson += JSON.stringify({ ref: String(i), link: req }) + '\n';
    }

    const response = await fetch(`${CONTROL}/api/v1/links/bulk`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${API_KEY}`,
        'Content-Type': 'application/x-ndjson',
        'Idempotency-Key': `${PREFIX}-bulk-${RUN}-${start}`,
      },
      body: ndjson,
    });

    if (!response.ok) {
      const detail = await response.text();
      throw new Error(`Bulk seeding failed with ${response.status}: ${detail.slice(0, 400)}`);
    }

    const text = await response.text();
    const lines = text.split('\n').filter(Boolean);

    for (const line of lines) {
      let res;
      try {
        res = JSON.parse(line);
      } catch {
        continue; // skip non-json line
      }

      const slug = requested.get(String(res?.ref));
      if (slug === undefined) {
        continue;
      }

      // A row that failed because the link exists from an earlier run still names a usable slug. Any
      // other failure (an unsafe or unresolvable target, a validation error) means the link does not
      // exist, and a corpus naming it would turn its share of the traffic into 404s.
      if (!res.ok && !(typeof res.error === 'string' && res.error.endsWith('/slug-taken'))) {
        throw new Error(`Bulk row ${slug} was not created: ${String(res.error)} ${String(res.detail ?? '')}`.slice(0, 400));
      }

      if (res.ok && typeof res.short_url === 'string' && !res.short_url.endsWith(`/${slug}`)) {
        throw new Error(`Bulk row ${slug} was created under another slug; the corpus would be wrong.`);
      }

      slugs.push(slug);
    }

    process.stderr.write(`  bulk seeded ${Math.min(end, TOTAL)}/${TOTAL}\n`);
  }

  return slugs;
}

/** Runs the creations with a bounded number in flight. */
async function seedIndividual() {
  const slugs = new Array(TOTAL);
  let next = 0;
  let done = 0;

  async function worker() {
    for (;;) {
      const index = next++;

      if (index >= TOTAL) {
        return;
      }

      slugs[index] = await createLink(index);
      done++;

      if (done % 500 === 0) {
        process.stderr.write(`  seeded ${done}/${TOTAL}\n`);
      }
    }
  }

  await Promise.all(Array.from({ length: CONCURRENCY }, worker));

  return slugs;
}

process.stderr.write(`Seeding ${TOTAL} links (${HOT} hot) on ${HOST} via ${CONTROL}\n`);

const slugs = USE_BULK ? await seedBulk() : await seedIndividual();

if (slugs.length !== TOTAL || slugs.some((slug) => typeof slug !== 'string')) {
  fail(`seeding produced ${slugs.length} usable slugs, not ${TOTAL}; corpus.json was not written.`);
}

const corpus = {
  host: HOST,
  created_at: new Date().toISOString(),
  prefix: PREFIX,
  // 80 % of traffic goes to the top hundred. They are simply the first HOT slugs; which links
  // are hot does not matter, only that the same small set is hot for the whole run, because that is
  // what a cache hit rate of 95 % depends on.
  hot: slugs.slice(0, HOT),
  all: slugs,
};

await writeFile(OUTPUT, `${JSON.stringify(corpus, null, 2)}\n`, 'utf8');

process.stderr.write(`Wrote ${OUTPUT} with ${corpus.all.length} links, ${corpus.hot.length} hot.\n`);
process.stderr.write('Now run:  k6 run -e BASE_URL=https://' + HOST + ' resolve.js\n');

// ---------------------------------------------------------------------------------------------

function parseArgs(list) {
  const parsed = {};

  for (let i = 0; i < list.length; i++) {
    const item = list[i];

    if (!item.startsWith('--')) {
      continue;
    }

    const name = item.slice(2);
    const eq = name.indexOf('=');

    if (eq >= 0) {
      parsed[name.slice(0, eq)] = name.slice(eq + 1);
    } else {
      parsed[name] = list[++i];
    }
  }

  return parsed;
}

function required(name, environmentVariable) {
  const value = args[name] ?? process.env[environmentVariable];

  if (!value) {
    fail(`--${name} (or ${environmentVariable}) is required.`);
  }

  return value;
}

function fail(message) {
  process.stderr.write(`seed.js: ${message}\n`);
  exit(2);
}
