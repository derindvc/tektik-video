const hostName = "com.tektik.video";
const historyLimit = 40;
const parallelLimit = 3;
const defaultTitle = "TekTık Video · Açık videoyu indir";
const interrupted = "Tarayıcı bağlantısı kesildi. Video sayfasına dönüp yeniden deneyebilirsin.";
const setupMessage = "Windows yardımcısına bağlanılamadı. TekTık Video klasöründeki Kur.bat dosyasını çalıştırıp yeniden dene.";

let history = [];
let storageQueue = Promise.resolve();
let dashboardTab;
let dashboardOpening;
const active = new Map();
const restored = chrome.storage.local.get("history").then(({ history: stored }) => {
  if (!Array.isArray(stored)) return;

  history = stored.map((job) => job.state === "running"
    ? { ...job, state: "failed", message: interrupted }
    : job);
  if (stored.some((job) => job.state === "running")) saveHistory();
});

function saveHistory() {
  const snapshot = history.map((job) => ({ ...job }));
  storageQueue = storageQueue.catch(() => {}).then(() =>
    chrome.storage.local.set({ history: snapshot })
  );
  storageQueue.catch((error) => console.error("TekTık Video geçmişi kaydedilemedi", error));
}

function openDashboard() {
  dashboardOpening ??= showDashboard().finally(() => { dashboardOpening = undefined; });
  return dashboardOpening;
}

async function showDashboard() {
  if (dashboardTab !== undefined) {
    try {
      const tab = await chrome.tabs.get(dashboardTab);
      if (tab.url === chrome.runtime.getURL("downloads.html")) {
        await chrome.tabs.update(dashboardTab, { active: true });
        await chrome.windows.update(tab.windowId, { focused: true });
        return;
      }
    } catch {
      // The tab may have been closed since it was last shown.
    }
  }

  const tab = await chrome.tabs.create({ url: chrome.runtime.getURL("downloads.html") });
  dashboardTab = tab.id;
}

function updateBadge(lastState) {
  const jobs = [...active.values()];
  const onlyJob = jobs[0]?.job;
  let text = "";
  let color = "#6847e8";
  let title = defaultTitle;

  if (jobs.length > 1) {
    text = String(jobs.length);
    title = `TekTık Video · ${jobs.length} video indiriliyor`;
  } else if (onlyJob) {
    text = onlyJob.percent == null ? "↓" : `${onlyJob.percent}%`;
    title = onlyJob.percent == null
      ? "TekTık Video · Video hazırlanıyor"
      : `TekTık Video · Video indiriliyor (%${onlyJob.percent})`;
  } else if (lastState === "done") {
    text = "✓";
    color = "#06714e";
    title = "TekTık Video · İndirme tamamlandı";
  } else if (lastState === "failed") {
    text = "!";
    color = "#ac2242";
    title = "TekTık Video · Ayrıntılar için simgeye sağ tıklayıp İndirmeleri göster’i seç";
  }

  chrome.action.setBadgeBackgroundColor({ color });
  chrome.action.setBadgeText({ text });
  chrome.action.setTitle({ title });
}

function parseVideoUrl(value) {
  try {
    const url = new URL(value);
    if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) return null;
    url.hash = "";
    const domain = url.hostname.toLowerCase().replace(/^www\./, "");
    if (["youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "youtube-nocookie.com"].includes(domain)) {
      const path = url.pathname.split("/");
      const id = domain === "youtu.be"
        ? path[1]
        : url.pathname === "/watch"
          ? url.searchParams.get("v")
          : ["shorts", "live", "embed", "v"].includes(path[1]) ? path[2] : null;
      if (!id || !/^[\w-]{11}$/.test(id)) return null;
      return { url: `https://www.youtube.com/watch?v=${id}`, site: "YouTube" };
    }
    return { url: url.href, site: domain };
  } catch {
    return null;
  }
}

function addHistory(job) {
  let remainingFinished = historyLimit - active.size;
  history = [job, ...history].filter((entry) => entry.state === "running" || remainingFinished-- > 0);
  saveHistory();
}

async function beginDownload(candidate, title, fallback) {
  await restored;
  const page = parseVideoUrl(fallback);
  const media = parseVideoUrl(candidate) ?? page;
  if (media && active.has(media.url)) return;

  const label = title?.trim();
  const job = {
    id: crypto.randomUUID(),
    title: label && !/(?:https?:\/\/|www\.|[?&][\w-]+=)/i.test(label) ? label.slice(0, 180) : "Video",
    site: page?.site ?? media?.site ?? "",
    at: Date.now(),
    state: "running",
    percent: null
  };

  if (!media || active.size >= parallelLimit) {
    job.state = "failed";
    job.message = media
      ? "Üç indirme zaten devam ediyor. Birinin tamamlanmasını bekleyip yeniden dene."
      : "Bu adres bir video sayfası olarak açılamadı. Videoyu açtıktan sonra simgeye tıkla.";
    addHistory(job);
    updateBadge(job.state);
    await openDashboard();
    return;
  }

  let port;
  let finished = false;
  let lastProgressAt = 0;
  let retry = page?.url !== media.url ? page?.url : null;
  active.set(media.url, { job });
  addHistory(job);
  updateBadge();

  function finish(state, details) {
    if (finished) return;
    finished = true;
    Object.assign(job, details, { state });
    active.delete(media.url);
    saveHistory();
    updateBadge(state);
    port?.disconnect();
  }

  function connectionFailed(responded) {
    finish("failed", {
      message: responded ? interrupted : setupMessage,
      setup: !responded
    });
    if (!responded) openDashboard().catch(console.error);
  }

  function connect(url) {
    let responded = false;
    try {
      const connection = chrome.runtime.connectNative(hostName);
      port = connection;
      connection.onDisconnect.addListener(() => {
        // Reading lastError prevents Chromium from logging a routine disconnect error.
        void chrome.runtime.lastError;
        if (!finished && port === connection) connectionFailed(responded);
      });
      connection.onMessage.addListener((message) => {
        if (finished || port !== connection) return;
        responded = true;

        if (message.type === "progress") {
          const percent = Number.isFinite(message.percent)
            ? Math.round(Math.max(0, Math.min(message.percent, 99)))
            : null;
          const now = Date.now();
          if (percent === job.percent || now - lastProgressAt < 750) return;
          lastProgressAt = now;
          job.percent = percent;
          saveHistory();
          updateBadge();
        } else if (message.type === "completed") {
          const filename = typeof message.filename === "string" ? message.filename : "";
          finish("done", { filename: filename.slice(0, 260), percent: 100 });
        } else if (message.type === "error") {
          if (retry && job.percent === null) {
            const nextUrl = retry;
            retry = null;
            port = null;
            connection.disconnect();
            connect(nextUrl);
            return;
          }
          const detail = typeof message.message === "string" ? message.message : "";
          finish("failed", { message: detail.slice(0, 450) || "Video indirilemedi." });
        }
      });
      connection.postMessage({ type: "download", url });
    } catch {
      connectionFailed(responded);
    }
  }
  connect(media.url);
}

function installMenus() {
  const webPages = ["http://*/*", "https://*/*"];
  chrome.contextMenus.create({
    id: "download-video",
    title: "Bu videoyu indir",
    contexts: ["video"],
    documentUrlPatterns: webPages
  });
  chrome.contextMenus.create({
    id: "download-link",
    title: "Bu bağlantıdaki videoyu indir",
    contexts: ["link"],
    documentUrlPatterns: webPages
  });
  chrome.contextMenus.create({
    id: "show-downloads",
    title: "İndirmeleri göster",
    contexts: ["action"]
  });
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(installMenus);
});

function findDirectVideo() {
  const candidates = Array.from(document.querySelectorAll("video"))
    .map((video) => {
      const bounds = video.getBoundingClientRect();
      const width = Math.min(bounds.right, innerWidth) - Math.max(bounds.left, 0);
      const height = Math.min(bounds.bottom, innerHeight) - Math.max(bounds.top, 0);
      return { url: video.currentSrc || video.src, area: width * height, visible: width > 0 && height > 0 };
    })
    .filter(({ url, visible }) => visible && /^https?:\/\//i.test(url)
      && /\.(mp4|webm|mov|m4v|ogv)(?:[?#]|$)/i.test(url))
    .sort((first, second) => second.area - first.area);
  return candidates[0]?.url ?? null;
}

async function downloadFromTab(tab) {
  const page = parseVideoUrl(tab.url);
  let direct;
  if (page && page.site !== "YouTube" && tab.id !== undefined) {
    try {
      const [result] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        func: findDirectVideo
      });
      direct = result?.result;
    } catch {
      // Some websites disallow script injection. The page URL still works for supported sites.
    }
  }
  await beginDownload(direct, tab.title, tab.url);
}

chrome.action.onClicked.addListener((tab) => {
  downloadFromTab(tab).catch(console.error);
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === "show-downloads") {
    openDashboard().catch(console.error);
  } else if (info.menuItemId === "download-video") {
    const page = info.frameUrl || info.pageUrl || tab?.url;
    const source = parseVideoUrl(page)?.site === "YouTube" ? page : info.srcUrl;
    beginDownload(source, tab?.title, page).catch(console.error);
  } else if (info.menuItemId === "download-link") {
    beginDownload(info.linkUrl).catch(console.error);
  }
});
