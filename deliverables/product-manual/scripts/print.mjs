import { chromium } from "playwright-core";
import { access, mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { server } from "./serve.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const out = path.join(root, "out");
await mkdir(path.join(out, "qa"), { recursive: true });
const candidates = [
  process.env.CHROMIUM_PATH,
  path.join(
    process.env.LOCALAPPDATA,
    "ms-playwright/chromium-1248/chrome-win64/chrome.exe",
  ),
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
].filter(Boolean);
let executablePath;
for (const candidate of candidates) {
  try {
    await access(candidate);
    executablePath = candidate;
    break;
  } catch {}
}
if (!executablePath)
  throw new Error(
    "Set CHROMIUM_PATH to an installed Chromium browser executable.",
  );
const browser = await chromium.launch({ executablePath, headless: true });
const page = await browser.newPage({
  viewport: { width: 1080, height: 1300 },
  deviceScaleFactor: 1.5,
});
const errors = [];
page.on("pageerror", (error) => errors.push(String(error)));
page.on("response", (response) => {
  if (response.status() >= 400)
    errors.push(`${response.status()} ${response.url()}`);
});
try {
  await page.goto("http://127.0.0.1:4173", {
    waitUntil: "networkidle",
    timeout: 90000,
  });
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all([...document.images].map((image) => image.decode()));
  });
  await page.emulateMedia({ media: "print" });
  const audit = await page.evaluate(() =>
    [...document.querySelectorAll(".page")].map((section, index) => {
      const pageRect = section.getBoundingClientRect();
      const footerRect = section
        .querySelector(".page-footer")
        .getBoundingClientRect();
      const body = section.querySelector(".page-body");
      const outside = [...body.querySelectorAll("*")]
        .filter((element) => {
          if (!element.getClientRects().length) return false;
          const box = element.getBoundingClientRect();
          return (
            box.bottom > footerRect.top - 8 ||
            box.left < pageRect.left - 2 ||
            box.right > pageRect.right + 2
          );
        })
        .map((element) => ({
          tag: element.tagName,
          class: element.className,
          text: element.textContent.slice(0, 65),
        }));
      return {
        page: index + 1,
        width: pageRect.width,
        height: pageRect.height,
        bodyBottom: body.getBoundingClientRect().bottom - pageRect.top,
        footerTop: footerRect.top - pageRect.top,
        outside,
      };
    }),
  );
  await writeFile(
    path.join(out, "qa/layout-audit.json"),
    JSON.stringify(
      { browser: await browser.version(), errors, pages: audit },
      null,
      2,
    ),
  );
  for (let i = 0; i < audit.length; i++) {
    await page
      .locator(".page")
      .nth(i)
      .screenshot({
        path: path.join(
          out,
          "qa",
          `page-${String(i + 1).padStart(2, "0")}.png`,
        ),
      });
  }
  const pdf = path.join(out, "InkCanvas-产品说明书-A4.pdf");
  await page.pdf({
    path: pdf,
    format: "A4",
    preferCSSPageSize: true,
    printBackground: true,
    displayHeaderFooter: false,
    tagged: true,
    outline: true,
  });
  console.log(
    JSON.stringify(
      {
        pdf,
        pages: audit.length,
        errors,
        overflow: audit.filter((item) => item.outside.length),
      },
      null,
      2,
    ),
  );
  if (errors.length || audit.some((item) => item.outside.length))
    process.exitCode = 1;
} finally {
  await browser.close();
  server.close();
}
