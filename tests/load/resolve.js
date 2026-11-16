// Deep Link Engine — resolve path load profile.
//
// The two scenarios and the three thresholds below are not to be relaxed to make a run pass: they
// are the resolve latency and error budgets, and a run that misses them is a release that does not
// go out.
//
//   steady   constant-arrival-rate, 2000 requests a second for ten minutes
//   spike    ramping-arrival-rate,  0 → 12000/s over 30 s, held 2 min, back to 2000/s over 1 min
//
//   p(95) < 25 ms and p(99) < 50 ms on the steady scenario
//   http_req_failed rate < 0.001 across the whole run
//
// Traffic mix: 80 % of requests hit one of the hundred hottest links, 15 % hit a random
// existing link, 5 % ask for a slug that does not exist — the last of those is deliberate, because
// the 404 path and the anti-enumeration budget are part of what is being measured, not noise
// to be excluded. 12 % of all requests carry a crawler user agent.
//
// Run it against a seeded instance. Seed first: seed.js creates the links through the control plane
// and writes corpus.json next to this file. It needs the control-plane URL, an API key with the
// links:write scope, the id of a verified link domain and that domain's host, as environment
// variables or as the matching --control, --api-key, --domain-id and --host flags:
//
//   DLE_CONTROL_URL=https://control.example.com DLE_API_KEY=dle_<prefix>_<secret> \
//   DLE_DOMAIN_ID=<domain id> DLE_LINK_HOST=link.example.com node seed.js
//   k6 run -e BASE_URL=https://link.example.com resolve.js
//
// The profile needs a deployed edge with PostgreSQL and Valkey behind it; release.yml runs it against
// staging as the k6 gate. The numbers only mean something when corpus.json was seeded against the
// instance under test and the edge does not throttle the generator. On a test or staging instance:
//
//   - set Dle:RateLimits:Edge:Enabled to false, which removes the resolve and QR limiters only;
//   - raise Dle:RateLimits:Edge:NotFound (TokensPerPeriod and Burst, per replenishment period)
//     above the miss stream, or spread the generator across source networks. The 404 budget is
//     always on: about 100 misses a second from one /24 (IPv4) or /48 (IPv6) drain the default
//     bucket in under a second, and the prefix is then shadow banned for
//     Dle:RateLimits:Edge:NotFound:ShadowBanMinutes. During the ban every request from it, hits
//     included, is answered 404, and the run fails dle_correct_outcome.

import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { SharedArray } from 'k6/data';

// Tell k6 that 200, 302, 404, and 429 are valid expected outcomes in this profile.
// The 5 % miss stream deliberately tests the negative cache and 404/429 paths.
// dle_correct_outcome independently asserts the strict expected status per request.
http.setResponseCallback(http.expectedStatuses(200, 302, 404, 429));

// Inlined rather than imported from jslib.k6.io. A remote import means the load test cannot run
// without internet access and pulls an unpinned third-party module into the process on every run,
// which is the shape of a supply-chain attack — for three lines of arithmetic.
function randomIntBetween(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

// ---------------------------------------------------------------------------------------------
// Corpus
// ---------------------------------------------------------------------------------------------

// SharedArray keeps one copy of the corpus per process rather than one per virtual user. At the
// 12 000/s spike there are thousands of VUs, and a per-VU copy of a hundred thousand slugs is how a
// load generator runs out of memory and reports latency that is its own.
const corpus = new SharedArray('links', function () {
  const data = JSON.parse(open('./corpus.json'));

  if (!data.hot || data.hot.length === 0 || !data.all || data.all.length === 0) {
    throw new Error(
      'corpus.json has no links. Run `node seed.js` against the target instance first, with ' +
        'DLE_CONTROL_URL, DLE_API_KEY (links:write scope), DLE_DOMAIN_ID and DLE_LINK_HOST set ' +
        '(or the --control, --api-key, --domain-id and --host flags).',
    );
  }

  return [data];
})[0];

const HOT = corpus.hot;
const ALL = corpus.all;
const HOST = corpus.host;

// ---------------------------------------------------------------------------------------------
// Client population
// ---------------------------------------------------------------------------------------------

// Real user agents, because the classifier's cost depends on which branch of the parser a string
// takes and a single synthetic agent would measure one branch and cache it forever.
const BROWSER_AGENTS = [
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
  'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 340.0.0.24.109',
  'Mozilla/5.0 (Linux; Android 15; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36 [FB_IAB/FB4A;FBAV/470.0.0.36.109;]',
];

// The common link preview crawlers, WhatsApp included. A confirmed crawler is served an Open Graph document instead
// of a redirect, which is a different and more expensive path — 12 % of it is part of the profile
// precisely because it is the path a naive benchmark leaves out.
const CRAWLER_AGENTS = [
  'facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)',
  'Twitterbot/1.0',
  'Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)',
  'LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)',
  'Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)',
  'WhatsApp/2.24.1.78 A',
];

const LANGUAGES = ['en-GB,en;q=0.9', 'sk-SK,sk;q=0.9,en;q=0.8', 'cs-CZ,cs;q=0.9', 'de-DE,de;q=0.9'];

// ---------------------------------------------------------------------------------------------
// Custom metrics
//
// http_req_duration answers "was it fast"; these answer "was it fast for the right reason". A run
// that meets p(95) by serving 404s is not a run that met p(95).
// ---------------------------------------------------------------------------------------------

const hotLatency = new Trend('dle_hot_latency', true);
const coldLatency = new Trend('dle_cold_latency', true);
const missLatency = new Trend('dle_miss_latency', true);
const crawlerLatency = new Trend('dle_crawler_latency', true);

const redirects = new Counter('dle_redirects');
const previews = new Counter('dle_crawler_previews');
const notFound = new Counter('dle_not_found');
const rateLimited = new Counter('dle_rate_limited');
const unexpected = new Counter('dle_unexpected_status');

const correctOutcome = new Rate('dle_correct_outcome');

// ---------------------------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------------------------

export const options = {
  discardResponseBodies: false,

  scenarios: {
    steady: {
      executor: 'constant-arrival-rate',
      rate: 2000,
      timeUnit: '1s',
      duration: '10m',
      preAllocatedVUs: 200,
      maxVUs: 2000,
      exec: 'resolve',
    },
    spike: {
      executor: 'ramping-arrival-rate',
      startTime: '10m',
      startRate: 2000,
      timeUnit: '1s',
      preAllocatedVUs: 500,
      maxVUs: 6000,
      stages: [
        { target: 12000, duration: '30s' },
        { target: 12000, duration: '2m' },
        { target: 2000, duration: '1m' },
      ],
      exec: 'resolve',
    },
  },

  thresholds: {
    // The latency and error budgets.
    'http_req_duration{scenario:steady}': ['p(95)<25', 'p(99)<50'],
    http_req_failed: ['rate<0.001'],

    // The spike has no steady-state latency threshold, but p99 must stay under 200 ms while the
    // autoscaler catches up, and that is what this asserts.
    'http_req_duration{scenario:spike}': ['p(99)<200'],

    // Every response has to be the response that request should have got. Without this a run can
    // hit every latency target while quietly answering 404 to everything.
    dle_correct_outcome: ['rate>0.999'],

    // The hot path is the one the latency budget is about, and it is the one that must be cheap.
    dle_hot_latency: ['p(95)<25', 'p(99)<50'],
  },

  // Connections are reused, because a real client population does too and because a fresh TLS
  // handshake per request would measure the handshake rather than the resolve path. Redirects are
  // not followed: 302 is the answer under test, and following it would measure somebody else's site.
  noVUConnectionReuse: false,
};

// ---------------------------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------------------------

const BASE_URL = (__ENV.BASE_URL || `https://${HOST}`).replace(/\/+$/, '');

// A slug that cannot exist: the miss stream has to be misses, not accidental hits, and it has to be
// distinct per request so that the negative cache is exercised the way an enumeration scan
// exercises it rather than answering one cached miss forever.
function missingSlug() {
  return `zz${exec.scenario.iterationInTest.toString(36)}${randomIntBetween(100000, 999999).toString(36)}`;
}

function pickSlug() {
  const roll = Math.random();

  if (roll < 0.8) {
    return { slug: HOT[randomIntBetween(0, HOT.length - 1)], kind: 'hot' };
  }

  if (roll < 0.95) {
    return { slug: ALL[randomIntBetween(0, ALL.length - 1)], kind: 'cold' };
  }

  return { slug: missingSlug(), kind: 'miss' };
}

export function resolve() {
  const { slug, kind } = pickSlug();
  const isCrawler = Math.random() < 0.12;

  const headers = {
    'User-Agent': isCrawler
      ? CRAWLER_AGENTS[randomIntBetween(0, CRAWLER_AGENTS.length - 1)]
      : BROWSER_AGENTS[randomIntBetween(0, BROWSER_AGENTS.length - 1)],
    'Accept-Language': LANGUAGES[randomIntBetween(0, LANGUAGES.length - 1)],
    Accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
  };

  // A campaign link in the wild carries UTM parameters, and they are on the forwardable allow list,
  // so leaving them out would skip the query-building branch entirely.
  const query = kind === 'miss' ? '' : '?utm_source=k6&utm_medium=load&utm_campaign=profile';

  const response = http.get(`${BASE_URL}/${slug}${query}`, {
    headers,
    redirects: 0,
    tags: { kind, crawler: isCrawler ? 'yes' : 'no' },
  });

  record(response, kind, isCrawler);
}

function record(response, kind, isCrawler) {
  const status = response.status;
  const duration = response.timings.duration;

  if (isCrawler) {
    crawlerLatency.add(duration);
  } else if (kind === 'hot') {
    hotLatency.add(duration);
  } else if (kind === 'cold') {
    coldLatency.add(duration);
  } else {
    missLatency.add(duration);
  }

  let expected;

  if (kind === 'miss') {
    // 429 is a correct answer to the one miss that drains a prefix's 404 budget (/24 for IPv4, /48
    // for IPv6), so it is counted separately rather than treated as a failure. What follows is not:
    // the prefix is then shadow banned, every request from it gets 404, hits included, and those
    // count as incorrect. Raise Dle:RateLimits:Edge:NotFound or spread the generator across source
    // networks before the run (see the header), and say so in the report.
    expected = status === 404 || status === 429;

    if (status === 404) {
      notFound.add(1);
    } else if (status === 429) {
      rateLimited.add(1);
    }
  } else if (isCrawler) {
    // A confirmed crawler gets an Open Graph document, not a redirect.
    expected = status === 200;
    previews.add(status === 200 ? 1 : 0);
  } else {
    // Never 301, which browsers and intermediaries cache indefinitely. A 200 here is the
    // interstitial, which the in-app agents in the population legitimately produce.
    expected = status === 302 || status === 200;
    redirects.add(status === 302 ? 1 : 0);
  }

  if (!expected) {
    unexpected.add(1);
  }

  correctOutcome.add(expected);

  check(response, {
    'never answers 301': (r) => r.status !== 301,
    'never answers 5xx': (r) => r.status < 500,
    'redirect names a target': (r) => r.status !== 302 || !!r.headers.Location,
  });
}

// ---------------------------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------------------------

export function handleSummary(data) {
  // The two acceptance numbers that k6 cannot see from outside — the cache hit ratio and
  // the dropped-event counter — are read from the instance's own metrics endpoint. They are printed
  // here as a reminder rather than asserted, because scraping Prometheus from inside a load test
  // would add a request to every iteration.
  const reminder = [
    '',
    'Acceptance criteria k6 cannot decide on its own:',
    '  cache hit rate >= 95 %       dle_cache_hit_ratio{level="l1"} + {level="l2"} on the edge',
    '  dropped click events = 0     dle_click_events_dropped_total must not have moved during the run',
    '  p99 <= 200 ms while scaling  the spike scenario',
    '',
  ].join('\n');

  return {
    stdout: reminder,
    'summary.json': JSON.stringify(data, null, 2),
  };
}
