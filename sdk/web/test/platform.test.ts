import { describe, expect, it } from 'vitest';

import { CHANNELS, detect, detectChannel, detectOsVersion, detectPlatform, requiresUserTapForAppLink } from '../src/platform';
import type { Channel, DevicePlatform } from '../src/types';

/** Copied verbatim from src/Dle.Domain/Routing/ChannelNames.cs (minus the server-only `unknown`). */
const CHANNEL_NAMES_CS = [
  'browser',
  'crawler',
  'in_app_fb',
  'in_app_ig',
  'in_app_tiktok',
  'in_app_linkedin',
  'in_app_snapchat',
  'in_app_x',
  'in_app_whatsapp',
  'in_app_telegram',
  'in_app_pinterest',
  'in_app_other',
  'app',
] as const;

interface Row {
  readonly name: string;
  readonly ua: string;
  readonly platform: DevicePlatform;
  readonly channel: Channel;
  readonly osVersion?: string;
  readonly hints?: { maxTouchPoints?: number; platform?: string };
}

const TABLE: readonly Row[] = [
  // --- ordinary browsers ---------------------------------------------------------------
  {
    name: 'iOS 17 Safari',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1',
    platform: 'ios',
    channel: 'browser',
    osVersion: '17.5',
  },
  {
    name: 'iPadOS classic UA',
    ua: 'Mozilla/5.0 (iPad; CPU OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1',
    platform: 'ios',
    channel: 'browser',
    osVersion: '16.6',
  },
  {
    name: 'iPadOS desktop-mode UA (Mac UA + touch)',
    ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15',
    platform: 'ios',
    channel: 'browser',
    hints: { maxTouchPoints: 5, platform: 'MacIntel' },
  },
  {
    name: 'Chrome on iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1',
    platform: 'ios',
    channel: 'browser',
    osVersion: '17.4',
  },
  {
    name: 'Firefox on iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/125.0 Mobile/15E148 Safari/605.1.15',
    platform: 'ios',
    channel: 'browser',
  },
  {
    name: 'Android 14 Chrome',
    ua: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.82 Mobile Safari/537.36',
    platform: 'android',
    channel: 'browser',
    osVersion: '14',
  },
  {
    name: 'Android 15 Chrome (os_version "15" of the B.7.2 literal)',
    ua: 'Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36',
    platform: 'android',
    channel: 'browser',
    osVersion: '15',
  },
  {
    name: 'Samsung Internet',
    ua: 'Mozilla/5.0 (Linux; Android 13; SAMSUNG SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/23.0 Chrome/115.0.0.0 Mobile Safari/537.36',
    platform: 'android',
    channel: 'browser',
    osVersion: '13',
  },
  {
    name: 'Firefox on Android',
    ua: 'Mozilla/5.0 (Android 14; Mobile; rv:125.0) Gecko/125.0 Firefox/125.0',
    platform: 'android',
    channel: 'browser',
    osVersion: '14',
  },
  {
    name: 'Windows Chrome',
    ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
    platform: 'desktop',
    channel: 'browser',
  },
  {
    name: 'Windows Edge',
    ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.2478.67',
    platform: 'desktop',
    channel: 'browser',
  },
  {
    name: 'macOS Safari (no touch)',
    ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4.1 Safari/605.1.15',
    platform: 'desktop',
    channel: 'browser',
    hints: { maxTouchPoints: 0, platform: 'MacIntel' },
  },
  {
    name: 'Linux Firefox',
    ua: 'Mozilla/5.0 (X11; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0',
    platform: 'desktop',
    channel: 'browser',
  },
  {
    name: 'ChromeOS',
    ua: 'Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
    platform: 'desktop',
    channel: 'browser',
  },
  // --- in-app webviews, one per ChannelNames member --------------------------------------
  {
    name: 'Facebook iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/460.0.0.36.107;FBBV/584622427;FBDV/iPhone15,3;FBMD/iPhone;FBSN/iOS;FBSV/17.4.1;FBSS/3;FBID/phone;FBLC/en_US;FBOP/5;FBRV/0]',
    platform: 'ios',
    channel: 'in_app_fb',
    osVersion: '17.4',
  },
  {
    name: 'Facebook Android',
    ua: 'Mozilla/5.0 (Linux; Android 14; SM-S911B Build/UP1A.231005.007; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/124.0.6367.54 Mobile Safari/537.36 [FB_IAB/FB4A;FBAV/460.0.0.38.109;]',
    platform: 'android',
    channel: 'in_app_fb',
  },
  {
    name: 'Messenger iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/MessengerForiOS;FBAV/450.0.0.34.108;FBBV/571215530;FBDV/iPhone14,5;FBMD/iPhone;FBSN/iOS;FBSV/17.4;FBSS/3;FBID/phone;FBLC/en_US;FBOP/5]',
    platform: 'ios',
    channel: 'in_app_fb',
  },
  {
    name: 'Instagram iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 330.0.3.29.91 (iPhone15,3; iOS 17_4_1; en_US; en; scale=3.00; 1179x2556; 590166283)',
    platform: 'ios',
    channel: 'in_app_ig',
  },
  {
    name: 'Instagram Android',
    ua: 'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP1A.240405.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/124.0.6367.54 Mobile Safari/537.36 Instagram 330.0.0.40.92 Android (34/14; 420dpi; 1080x2274; Google/google; Pixel 8; shiba; shiba; en_US; 596052253)',
    platform: 'android',
    channel: 'in_app_ig',
  },
  {
    name: 'TikTok iOS (musical_ly_ token, no trailing word boundary)',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 musical_ly_34.5.0 JsSdk/2.0 NetType/WIFI Channel/App Store ByteLocale/en Region/US isDarkMode/1 WKWebView/1 RevealType/Dialog BytedanceWebview/d8a21c6',
    platform: 'ios',
    channel: 'in_app_tiktok',
  },
  {
    name: 'TikTok Android (trill_ token)',
    ua: 'Mozilla/5.0 (Linux; Android 13; SM-S908B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/123.0.6312.118 Mobile Safari/537.36 trill_2023407040 JsSdk/1.0 NetType/WIFI Channel/googleplay AppName/musical_ly app_version/34.7.4 ByteLocale/en Region/US BytedanceWebview/d8a21c6',
    platform: 'android',
    channel: 'in_app_tiktok',
  },
  {
    name: 'LinkedIn iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [LinkedInApp]',
    platform: 'ios',
    channel: 'in_app_linkedin',
  },
  {
    name: 'LinkedIn Android',
    ua: 'Mozilla/5.0 (Linux; Android 14; Pixel 7 Build/UQ1A.240205.004; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/122.0.6261.119 Mobile Safari/537.36 LinkedIn/9.29.5183',
    platform: 'android',
    channel: 'in_app_linkedin',
  },
  {
    name: 'Snapchat iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_3_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Snapchat/12.80.0.30 (iPhone14,2; iOS 17.3.1; gzip)',
    platform: 'ios',
    channel: 'in_app_snapchat',
  },
  {
    name: 'X (Twitter) iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Twitter for iPhone/10.34',
    platform: 'ios',
    channel: 'in_app_x',
  },
  {
    name: 'X (Twitter) Android',
    ua: 'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP1A.240405.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/124.0.6367.54 Mobile Safari/537.36 TwitterAndroid',
    platform: 'android',
    channel: 'in_app_x',
  },
  {
    name: 'WhatsApp Android in-app browser (Mozilla prefix, WhatsApp token)',
    ua: 'Mozilla/5.0 (Linux; Android 13; SM-G991B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/122.0.6261.105 Mobile Safari/537.36 WhatsApp/2.24.5.76 A',
    platform: 'android',
    channel: 'in_app_whatsapp',
  },
  {
    name: 'Telegram Android',
    ua: 'Mozilla/5.0 (Linux; Android 13; Pixel 6 Build/TQ3A.230805.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/117.0.0.0 Mobile Safari/537.36 Telegram-Android/10.1.1 (Google Pixel 6; Android 13; SDK 33; HIGH)',
    platform: 'android',
    channel: 'in_app_telegram',
  },
  {
    name: 'Telegram iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Telegram-iOS/10.1',
    platform: 'ios',
    channel: 'in_app_telegram',
  },
  {
    name: 'Pinterest iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [Pinterest/iOS]',
    platform: 'ios',
    channel: 'in_app_pinterest',
  },
  {
    name: 'Pinterest Android',
    ua: 'Mozilla/5.0 (Linux; Android 13; SM-S901B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/122.0.6261.105 Mobile Safari/537.36 [Pinterest/Android]',
    platform: 'android',
    channel: 'in_app_pinterest',
  },
  {
    name: 'LINE iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Safari Line/13.13.0',
    platform: 'ios',
    channel: 'in_app_other',
  },
  {
    name: 'WeChat Android',
    ua: 'Mozilla/5.0 (Linux; Android 12; M2102J20SG Build/SKQ1.211006.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/116.0.0.0 Mobile Safari/537.36 XWEB/1160065 MMWEBSDK/20231202 MMWEBID/1234 MicroMessenger/8.0.47.2560(0x28002F35) WeChat/arm64',
    platform: 'android',
    channel: 'in_app_other',
  },
  {
    name: 'Google Search App iOS',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) GSA/318.0.618896688 Mobile/15E148 Safari/604.1',
    platform: 'ios',
    channel: 'in_app_other',
  },
  {
    name: 'Android generic WebView (; wv)',
    ua: 'Mozilla/5.0 (Linux; Android 13; SM-A536B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/124.0.6367.82 Mobile Safari/537.36',
    platform: 'android',
    channel: 'in_app_other',
  },
  {
    name: 'iOS generic WKWebView (no Safari token)',
    ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148',
    platform: 'ios',
    channel: 'in_app_other',
  },
  // --- crawlers ----------------------------------------------------------------------------
  {
    name: 'facebookexternalhit',
    ua: 'facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)',
    platform: 'other',
    channel: 'crawler',
  },
  { name: 'Twitterbot', ua: 'Twitterbot/1.0', platform: 'other', channel: 'crawler' },
  {
    name: 'Slackbot',
    ua: 'Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)',
    platform: 'other',
    channel: 'crawler',
  },
  {
    name: 'LinkedInBot beats in_app_linkedin',
    ua: 'LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)',
    platform: 'other',
    channel: 'crawler',
  },
  {
    name: 'Discordbot',
    ua: 'Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)',
    platform: 'other',
    channel: 'crawler',
  },
  { name: 'TelegramBot beats in_app_telegram', ua: 'TelegramBot (like TwitterBot)', platform: 'other', channel: 'crawler' },
  {
    name: 'WhatsApp preview fetcher (no Mozilla prefix) beats in_app_whatsapp',
    ua: 'WhatsApp/2.23.20.0 A',
    platform: 'other',
    channel: 'crawler',
  },
  {
    name: 'Googlebot smartphone',
    ua: 'Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
    platform: 'android',
    channel: 'crawler',
  },
  {
    name: 'Pinterestbot beats in_app_pinterest',
    ua: 'Mozilla/5.0 (compatible; Pinterestbot/1.0; +http://www.pinterest.com/bot.html)',
    platform: 'other',
    channel: 'crawler',
  },
];

describe('channel names mirror ChannelNames.cs', () => {
  it('exports exactly the server set', () => {
    expect([...CHANNELS].sort()).toEqual([...CHANNEL_NAMES_CS].sort());
  });

  it('every in-app channel appears in the UA table', () => {
    const covered = new Set(TABLE.map((row) => row.channel));
    for (const channel of CHANNEL_NAMES_CS) {
      if (channel.startsWith('in_app_')) expect(covered.has(channel), channel).toBe(true);
    }
  });

  it('has at least 25 real user agents', () => {
    expect(TABLE.length).toBeGreaterThanOrEqual(25);
  });
});

describe('user-agent table', () => {
  it.each(TABLE.map((row) => [row.name, row] as const))('%s', (_name, row) => {
    expect(detectPlatform(row.ua, row.hints)).toBe(row.platform);
    expect(detectChannel(row.ua)).toBe(row.channel);
    if (row.osVersion !== undefined) expect(detectOsVersion(row.ua)).toBe(row.osVersion);
    const info = detect(row.ua, row.hints);
    expect(info.channel).toBe(row.channel);
    expect(info.isInAppWebView).toBe(row.channel.startsWith('in_app_'));
    expect(info.isCrawler).toBe(row.channel === 'crawler');
  });
});

describe('edge cases', () => {
  it('reads the current navigator when no UA is supplied', () => {
    const info = detect();
    expect(typeof info.platform).toBe('string');
    expect(info.channel).toBe('browser');
  });

  it('classifies an empty UA as other/browser', () => {
    expect(detect('')).toEqual({ platform: 'other', channel: 'browser', isInAppWebView: false, isCrawler: false });
  });

  it('does not report a desktop OS version', () => {
    expect(detectOsVersion('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15')).toBeUndefined();
  });

  it('requires a user tap for an app link only inside a mobile in-app webview', () => {
    expect(requiresUserTapForAppLink({ channel: 'in_app_ig', platform: 'ios' })).toBe(true);
    expect(requiresUserTapForAppLink({ channel: 'in_app_fb', platform: 'android' })).toBe(true);
    expect(requiresUserTapForAppLink({ channel: 'browser', platform: 'ios' })).toBe(false);
    expect(requiresUserTapForAppLink({ channel: 'in_app_other', platform: 'desktop' })).toBe(false);
  });
});
