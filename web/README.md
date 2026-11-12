# The Elio website

The site for [elio.sh](https://elio.sh). It is plain static files: no build step, no dependencies,
no requests to anywhere else, no cookies and no tracking.

## Preview

```bash
dotnet run --project web/Elio.Web --urls http://127.0.0.1:5199
```

Then open http://127.0.0.1:5199. `Elio.Web` is a few lines that serve `wwwroot` as it is; it exists
only so the site opens from the solution. Opening `wwwroot/index.html` straight from the disk works
too.

## Publish

Upload the **contents of `web/Elio.Web/wwwroot`** to the web host, so that `index.html` is at the
root. Nothing else in this folder is needed on the server. If the host lets you choose the page for
addresses that do not exist, point it at `404.html`.

All links inside the site are relative, so it also works from a sub-folder.

## Links to the repository and the downloads

The page links to the source at `https://github.com/localtonet/elio` and to the newest builds at
`https://github.com/localtonet/elio/releases/latest/download/<name>`. Those download links work because the release workflow gives
every file a name without the version in it (`elio-win-x64.zip`, `elio-osx-arm64.zip`, `elio-linux-x64.deb`,
`elio-android.apk` and so on). If a file is renamed in `.github/workflows/release.yml`, change it in
`wwwroot/index.html` too. If the repository moves, search and replace its address there.

Two more things to know:

- `og:image` and `twitter:image` are relative (`img/social.png`) so the site works from any folder.
  Some social networks only take a full address: once the site is on its domain, change both to
  `https://elio.sh/img/social.png`.
- The canonical address, `robots.txt` and `sitemap.xml` say `https://elio.sh/`. Change them if the
  site lives elsewhere.

## What is in it

| File | |
| --- | --- |
| `index.html` | The whole site, one page |
| `404.html` | Shown for an address that does not exist. Self-contained, styles included |
| `css/site.css`, `js/site.js` | The styles, and a few extras (copy buttons, the current section in the header). The page reads the same without JavaScript |
| `img/*.webp` | Pictures of the real app, taken off screen. `-dark` and `-light` pairs follow the visitor's colour scheme |
| `img/social.png` | The preview picture for links shared on social networks, 1200 × 630 |
| `favicon.svg`, `img/icon-*.png`, `img/apple-touch-icon.png`, `img/logo.svg` | The icon and the mark |
| `site.webmanifest`, `robots.txt`, `sitemap.xml` | For browsers and search engines |

The file browser, the shared-terminal diagram and the "adding a device" steps are drawn in HTML and
labelled as illustrations on the page; they are not screenshots.
