# Deep Link Engine — Brand & Visual Identity Guidelines

This guide details the brand identity, color palettes, vector assets, and typography system for Deep Link Engine (DLE).

---

## 1. Visual Identity Concept

Deep Link Engine is the European, self-hosted, cryptographically sovereign alternative to Firebase Dynamic Links, Branch, and AppsFlyer. 

The visual mark represents:
- **Interlocking Hyper-Links**: Two interlocking precision nodes representing source web traffic and destination mobile native apps.
- **The Core Beam**: A high-speed diagonal ray symbolizing zero-latency edge link resolution (sub-10ms).
- **The Focal Point**: A bright spark node representing deterministic deferred attribution (Play Install Referrer, iOS claim codes).
- **European Sovereignty**: Deep space slate navy, electric royal blue, and cyan neon accents reflecting European privacy standards and technical precision.

---

## 2. Color Palette

### Primary Brand Colors

| Swatch | Name | HEX | RGB | Use Case |
|---|---|---|---|---|
| <span style="color:#0f172a">■</span> | **Deep Space Navy** | `#0f172a` | `rgb(15, 23, 42)` | Primary dark background, brand contrast |
| <span style="color:#2563eb">■</span> | **Electric Royal Blue** | `#2563eb` | `rgb(37, 99, 235)` | Primary brand color, core gradient |
| <span style="color:#38bdf8">■</span> | **Vivid Sky Cyan** | `#38bdf8` | `rgb(56, 189, 248)` | Neon accent, link ray, hover states |
| <span style="color:#4f46e5">■</span> | **Sovereign Indigo** | `#4f46e5` | `rgb(79, 70, 229)` | Depth gradient, secondary contrast |
| <span style="color:#ffffff">■</span> | **Pure Signal White** | `#ffffff` | `rgb(255, 255, 255)` | Focal sparks, dark mode typography |

### UI Neutrals & Semantic Tones

| Swatch | Name | HEX | Purpose |
|---|---|---|---|
| <span style="color:#1e293b">■</span> | Slate Surface | `#1e293b` | Card backgrounds, badge containers |
| <span style="color:#334155">■</span> | Slate Border | `#334155` | Subtle dividers, card borders |
| <span style="color:#94a3b8">■</span> | Slate Muted | `#94a3b8` | Subtitles, secondary text |
| <span style="color:#10b981">■</span> | Verification Green | `#10b981` | App Link / Universal Link verified status |
| <span style="color:#f43f5e">■</span> | Security Red | `#f43f5e` | Abuse quarantine, rate limit block |

---

## 3. Asset Inventory

All official vector graphics are stored under [`docs/assets/`](.):

| File | Resolution | Description |
|---|---|---|
| [`logo.svg`](logo.svg) | 880 × 220 | Primary horizontal logo with wordmark (Dark Mode / Transparent) |
| [`logo-light.svg`](logo-light.svg) | 880 × 220 | Primary horizontal logo with wordmark (Light Mode) |
| [`logo-mark.svg`](logo-mark.svg) | 512 × 512 | Square icon mark / app squircle |
| [`banner.svg`](banner.svg) | 1200 × 380 | Hero documentation & GitHub README banner |
| [`og-social-preview.svg`](og-social-preview.svg) | 1200 × 630 | Open Graph card for GitHub, Twitter/X, and social unfurling |
| [`showcase.jpg`](showcase.jpg) | 1920 × 1080 | 3D illuminated concept art & promotional showcase |
| [`favicon.svg`](../../src/Dle.Edge/wwwroot/static/favicon.svg) | 32 × 32 | Web favicon for Edge redirect service and Admin Console |

---

## 4. Typography

* **Headings & Wordmark**: Modern geometric sans-serif (`system-ui`, `-apple-system`, `Inter`, `'Segoe UI'`).
* **Letter Spacing**:
  * Wordmark: `-0.025em` to `-0.03em` for tight, confident branding.
  * Taglines: `+0.12em` to `+0.18em` uppercase for premium technical precision.
* **Code & Technical Notation**: Monospace (`ui-monospace`, `SFMono-Regular`, `Consolas`, `monospace`).

---

## 5. Usage Guidelines

1. **Clear Space**: Maintain clear space equal to at least 25% of the mark's height around the logo.
2. **Contrast**: Use `logo.svg` on dark backgrounds (`#000000` to `#1e293b`) and `logo-light.svg` on light backgrounds (`#f8fafc` to `#ffffff`).
3. **No Distortion**: Never skew, stretch, or alter the aspect ratio of the vector mark.
