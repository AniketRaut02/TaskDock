const statusEl = document.getElementById("status");
const portEl = document.getElementById("port");

chrome.storage.local.get(["status", "port"]).then((s) => {
  statusEl.textContent = s.status || "Not connected";
  portEl.value = s.port || "";
});
chrome.storage.onChanged.addListener((c) => { if (c.status) statusEl.textContent = c.status.newValue; });
portEl.addEventListener("change", () => {
  const p = parseInt(portEl.value, 10);
  chrome.storage.local.set({ port: p > 0 && p < 65536 ? p : 0 });
});
