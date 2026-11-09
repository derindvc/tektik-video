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

function createRow(job) {
  const row = jobTemplate.content.firstElementChild.cloneNode(true);
  row.dataset.state = job.state;
  row.querySelector(".job-title").textContent = job.filename || job.title;
  row.querySelector(".job-meta").textContent = [job.site, dateFormat.format(job.at)]
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
