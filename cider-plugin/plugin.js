// Cider loads this module from %APPDATA%\sh.cider.dotnet\plugins\<identifier>\plugin.js and calls
// setup() each time its UI loads. Lyrider registers the lyrider: protocol in its MSIX package and
// quits on a protocol launch while another copy runs, so opening it again is harmless.
const identifier = "io.github.ayor1337.lyrider-launcher";
const launchUri = "lyrider://launch";
// sessionStorage outlives a UI reload but not a Cider restart: launch once per Cider run.
const launchedKey = `${identifier}/launched`;

function launchLyrider() {
    if (sessionStorage.getItem(launchedKey) === "true") {
        return;
    }

    // Cider 4's WebView2 host object; OpenInBrowser shell-executes the URI it is given.
    const windowControls = window.chrome?.webview?.hostObjects?.windowControls;
    if (!windowControls?.OpenInBrowser) {
        console.warn("[Lyrider Launcher] Cider host bridge is unavailable; Lyrider was not started.");
        return;
    }

    sessionStorage.setItem(launchedKey, "true");
    Promise.resolve(windowControls.OpenInBrowser(launchUri)).catch(error => {
        sessionStorage.removeItem(launchedKey);
        console.error("[Lyrider Launcher] Could not start Lyrider.", error);
    });
}

export default {
    name: "Lyrider Launcher",
    identifier,
    description: "Starts Lyrider when Cider starts.",
    version: "1.0.0",
    pluginKitVersion: "1.0.0",
    setup() {
        launchLyrider();
    }
};
