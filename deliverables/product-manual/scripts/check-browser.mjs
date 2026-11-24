import { chromium } from "playwright-core";
import { writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { server } from "./serve.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const executablePath =
  process.env.CHROMIUM_PATH ||
  path.join(
    process.env.LOCALAPPDATA,
    "ms-playwright/chromium-1248/chrome-win64/chrome.exe",
  );
const browser = await chromium.launch({ executablePath, headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 960 } });
const errors = [];
page.on("pageerror", (error) => errors.push(String(error)));
try {
  await page.goto("http://127.0.0.1:4173", { waitUntil: "networkidle" });
  await page.evaluate(() => document.fonts.ready);
  const fonts = await page.evaluate(() => ({
    chinese: document.fonts.check('16px "Noto Sans SC"', "课堂书写码道"),
    latin: document.fonts.check('16px "Source Sans"', "Ink Canvas 3.15.0"),
    icons: document.fonts.check('16px "Ink Icons"'),
  }));
  const pageCount = await page.locator(".page").count();
  const titlesMatchPages =
    (await page.locator("#page-select option").count()) === pageCount;
  await page.selectOption("#page-select", "7");
  await page.waitForFunction(() => {
    const box = document.getElementById("page-7").getBoundingClientRect();
    return box.top >= 75 && box.top < 150 && document.getElementById("page-select").value === "7";
  });
  const navigationWorks =
    (await page.locator("#page-select").inputValue()) === "7";
  await page.evaluate(() => {
    window.print = () => {
      window.__manualPrintCalled = true;
    };
  });
  await page.getByRole("button", { name: "打印 / 保存 PDF" }).click();
  const printWorks = await page.evaluate(
    () => window.__manualPrintCalled === true,
  );
  await page.screenshot({ path: path.join(root, "out/qa/web-desktop.png") });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => window.scrollTo(0, 0));
  const mobileFits = await page.evaluate(
    () => document.documentElement.scrollWidth <= innerWidth + 1,
  );
  await page.screenshot({ path: path.join(root, "out/qa/web-mobile.png") });

  const offline = await browser.newPage();
  await offline.goto(pathToFileURL(path.join(root, "dist/index.html")).href, {
    waitUntil: "load",
  });
  await offline.evaluate(() => document.fonts.ready);
  const localFileWorks = (await offline.locator(".page").count()) === pageCount;
  const localImagesWork = await offline.evaluate(() =>
    [...document.images].every(
      (image) => image.complete && image.naturalWidth > 0,
    ),
  );
  const caseDownloads = [];
  for (const link of await page.locator(".case-files a").all()) {
    const href = await link.getAttribute("href");
    const response = await page.request.get(new URL(href, page.url()).href);
    const body = await response.body();
    caseDownloads.push({
      href,
      status: response.status(),
      bytes: body.length,
      signature: body.subarray(0, 4).toString("ascii"),
    });
  }
  const caseDownloadsWork =
    caseDownloads.length === 2 &&
    caseDownloads.every((file) => file.status === 200 && file.bytes > 10000) &&
    caseDownloads[0].signature.startsWith("PK") &&
    caseDownloads[1].signature === "%PDF";
  const videos = [];
  for (const video of await page.locator("video").all()) {
    const result = await video.evaluate(async (element) => {
      element.load();
      await new Promise((resolve, reject) => {
        element.addEventListener("loadeddata", resolve, { once: true });
        element.addEventListener(
          "error",
          () => reject(new Error("Video load failed")),
          { once: true },
        );
        setTimeout(() => reject(new Error("Video timeout")), 15000);
      });
      await element.play();
      await new Promise((resolve) => setTimeout(resolve, 350));
      element.pause();
      return {
        duration: element.duration,
        currentTime: element.currentTime,
        width: element.videoWidth,
      };
    });
    videos.push(result);
  }
  const videosPlay =
    videos.length === 9 &&
    videos.every((video) => video.currentTime > 0 && video.width > 0);
  const offlineVideoPlays = await offline
    .locator("video")
    .first()
    .evaluate(async (element) => {
      await element.play();
      await new Promise((resolve) => setTimeout(resolve, 350));
      element.pause();
      return element.currentTime > 0 && element.videoWidth > 0;
    });
  const result = {
    fonts,
    pageCount,
    titlesMatchPages,
    navigationWorks,
    printWorks,
    mobileFits,
    localFileWorks,
    localImagesWork,
    caseDownloadsWork,
    caseDownloads,
    offlineVideoPlays,
    videosPlay,
    videos,
    errors,
  };
  await writeFile(
    path.join(root, "out/qa/browser-check.json"),
    JSON.stringify(result, null, 2),
  );
  console.log(result);
  if (
    Object.values(fonts).some((value) => !value) ||
    pageCount !== 28 ||
    !titlesMatchPages ||
    !navigationWorks ||
    !printWorks ||
    !mobileFits ||
    !localFileWorks ||
    !localImagesWork ||
    !caseDownloadsWork ||
    !offlineVideoPlays ||
    !videosPlay ||
    errors.length
  )
    process.exitCode = 1;
} finally {
  await browser.close();
  server.close();
}
