#!/usr/bin/env node
//
// Deep Link Engine — load-profile corpus seeder.
//
// Creates the links resolve.js needs and writes corpus.json next to it. Node rather than k6, for one
// reason: k6 has no way to write a file, and the corpus has to outlive the process that created it
// so that a run can be repeated against the same links. Node 24 is already a build dependency of the
// admin application, so this adds nothing to the tool chain.
//
//   node seed.js \
//     --control https://control.example.com \
//     --api-key dle_<prefix>_<secret> \
//     --domain-id 5d7c9e2b-4f16-4a8e-9c2d-8f4c1f0a77b1 \
//     --host link.example.com \
//     --total 10000 --hot 100
//
// The counts default to the profile's own needs: a hundred hot links, because §D.5 says the hot set
// is the top hundred, and ten thousand in total so that the 15 % "random existing" stream is spread
// widely enough to actually miss the cache sometimes. A corpus of a few hundred would sit entirely
// in L1 and the run would report a cache hit rate of 100 % that means nothing.
//
// NOT RUN in this repository's development environment: it needs a deployed control plane and a
// verified domain. See README.md.

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
 * shape §B.5.4 documents and the shape a real campaign has. A corpus of links whose rule set is a
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

  const response = await fetch(`${CONTROL}/api/v1/links`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${API_KEY}`,
      'Content-Type': 'application/json',
      // The same key with the same body replays the stored response, so re-running the seeder
      // against a partially seeded instance is idempotent rather than a wall of 409s (§B.7.3).
      'Idempotency-Key': `${PREFIX}-${index}`,
    },
    body: JSON.stringify(body),
  });

  if (response.status === 201 || response.status === 200) {
    return body.slug;
  }

  if (response.status === 409) {
    // Already there from an earlier run under a different idempotency key. Still usable.
    return body.slug;
  }

  const detail = await response.text();

  throw new Error(
    `Creating ${body.slug} failed with ${response.status}: ${detail.slice(0, 400)}`,
  );
}

/** Runs the creations with a bounded number in flight. */
async function seed() {
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

const slugs = await seed();

const corpus = {
  host: HOST,
  created_at: new Date().toISOString(),
  prefix: PREFIX,
  // §D.5: 80 % of traffic goes to the top hundred. They are simply the first HOT slugs; which links
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
