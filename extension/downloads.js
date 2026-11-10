const dateFormat = new Intl.DateTimeFormat("tr-TR", {
  dateStyle: "medium",
  timeStyle: "short"
});

const rows = document.querySelector("#jobs");
const empty = document.querySelector("#empty");
const activeCount = document.querySelector("#active-count");
const jobTemplate = document.querySelector("#job-template");
const folderButton = document.querySelector("#open-folder");
const folderMessage = document.querySelector("#folder-message");
const mode = document.querySelector("#mode");
const quality = document.querySelector("#quality");
const maxMinutes = document.querySelector("#max-minutes");
const folderPath = document.querySelector("#folder-path");
const chooseFolder = document.querySelector("#choose-folder");
const linkForm = document.querySelector("#link-form");
const linkUrl = document.querySelector("#link-url");
const linkMode = document.querySelector("#link-mode");
const linkSubmit = document.querySelector("#link-submit");
const linkMessage = document.querySelector("#link-message");

linkForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  linkSubmit.disabled = true;
  linkMessage.hidden = true;
  try {
    const hostname = new URL(linkUrl.value.trim()).hostname.toLowerCase();
    if (/(^|\.)spotify\.com$|^spotify\.link$|^spoti\.fi$/.test(hostname)) {
      linkMessage.textContent = "Spotify bağlantısından ses dosyası indirilemiyor. Spotify'da çevrimdışı dinleme özelliğini kullan.";
      linkMessage.dataset.state = "error";
      linkMessage.hidden = false;
      return;
    }
    const result = await chrome.runtime.sendMessage({
      type: "downloadUrl", url: linkUrl.value.trim(), mode: linkMode.value
    });
    linkMessage.textContent = result?.ok
      ? "İndirme başlatıldı. Durumunu aşağıdan takip edebilirsin."
      : result?.message || "İndirme başlatılamadı.";
    linkMessage.dataset.state = result?.ok ? "success" : "error";
    linkMessage.hidden = false;
    if (result?.ok) linkUrl.value = "";
  } catch (error) {
    const detail = String(error?.message || "");
    linkMessage.textContent = /context invalidated/i.test(detail)
      ? "Bu İndirmeler sekmesi güncellemeden önce açılmış. Sekmeyi kapatıp eklentiden yeniden aç."
      : /receiving end does not exist|message port closed|no response/i.test(detail)
        ? "Uzantının arka planı güncel değil. Uzantılar sayfasında TekTık Video için Yeniden yükle'ye bas, ardından bu sekmeyi yenile."
        : "Bağlantı kurulamadı. Uzantılar sayfasında TekTık Video'yu yeniden yükle ve İndirmeler ekranını tekrar aç.";
    linkMessage.dataset.state = "error";
    linkMessage.hidden = false;
  } finally {
    linkSubmit.disabled = false;
  }
});

chrome.storage.local.get("preferences").then(({ preferences = {} }) => {
  mode.value = preferences.mode ?? "video";
  quality.value = preferences.quality ?? "best";
  maxMinutes.value = String(preferences.maxMinutes ?? 0);
  quality.disabled = mode.value === "audio";
});

for (const control of [mode, quality, maxMinutes]) {
  control.addEventListener("change", () => {
    quality.disabled = mode.value === "audio";
    chrome.storage.local.set({ preferences: {
      mode: mode.value, quality: quality.value, maxMinutes: Number(maxMinutes.value)
    } });
  });
}

chrome.runtime.sendNativeMessage("com.tektik.video", { type: "getFolder" })
  .then((result) => { if (result?.type === "folder") folderPath.textContent = result.path; })
  .catch(() => {});

chooseFolder.addEventListener("click", async () => {
  chooseFolder.disabled = true;
  try {
    const result = await chrome.runtime.sendNativeMessage("com.tektik.video", { type: "chooseFolder" });
    if (result?.type === "folder") folderPath.textContent = result.path;
    else if (result?.type === "error") throw new Error(result.message);
  } catch (error) {
    folderMessage.textContent = error.message || "Klasör seçilemedi. Yerel yardımcıyı güncelle.";
    folderMessage.hidden = false;
  } finally {
    chooseFolder.disabled = false;
  }
});

function createRow(job) {
  const row = jobTemplate.content.firstElementChild.cloneNode(true);
  row.dataset.state = job.state;
  row.querySelector(".job-title").textContent = job.filename || job.title;
  const duration = Number.isFinite(job.duration) ? `${Math.floor(job.duration / 60)} dk ${job.duration % 60} sn` : null;
  const size = Number.isFinite(job.size) ? `yaklaşık ${(job.size / 1048576).toFixed(1)} MB` : null;
  row.querySelector(".job-meta").textContent = [job.site, duration, size, dateFormat.format(job.at)]
    .filter(Boolean).join(" · ");

  const status = row.querySelector(".job-status");
  const icon = row.querySelector("use");
  if (job.state === "running") {
    status.textContent = job.percent === null ? "Hazırlanıyor" : `%${job.percent}`;
    icon.setAttribute("href", "#icon-download");
    const progress = row.querySelector("progress");
    progress.hidden = false;
    if (job.percent !== null) progress.value = job.percent;
  } else if (job.state === "done") {
    status.textContent = "Tamamlandı";
    icon.setAttribute("href", "#icon-check");
  } else {
    status.textContent = job.setup ? "Kurulum gerekli" : "İndirilemedi";
    icon.setAttribute("href", "#icon-alert");
    const message = row.querySelector(".job-message");
    message.textContent = job.message;
    message.hidden = false;
  }
  return row;
}

function render(history = []) {
  const count = history.filter((job) => job.state === "running").length;
  activeCount.textContent = `${count} devam ediyor`;
  activeCount.hidden = count === 0;
  empty.hidden = history.length > 0;
  rows.replaceChildren(...history.map(createRow));
}

folderButton.addEventListener("click", async () => {
  folderButton.disabled = true;
  folderMessage.hidden = true;
  try {
    const result = await chrome.runtime.sendNativeMessage("com.tektik.video", { type: "openFolder" });
    if (result?.type !== "opened") {
      folderMessage.textContent = result?.message || "İndirme klasörü açılamadı.";
      folderMessage.hidden = false;
    }
  } catch {
    folderMessage.textContent = "Windows yardımcısına bağlanılamadı. TekTık Video klasöründeki Kur.bat dosyasını çalıştır.";
    folderMessage.hidden = false;
  } finally {
    folderButton.disabled = false;
  }
});

const loaded = chrome.storage.local.get("history").then(({ history }) => render(history));
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.history) loaded.then(() => render(changes.history.newValue));
});
