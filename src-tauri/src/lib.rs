
use std::{
    net::TcpStream,
    path::PathBuf,
    process::{Child, Command, Stdio},
    sync::Mutex,
    thread,
    time::{Duration, Instant},
};

use tauri::{path::BaseDirectory, AppHandle, Manager};

#[cfg(debug_assertions)]
const BACKEND_ADDRESS: &str = "127.0.0.1:5000";
#[cfg(not(debug_assertions))]
const BACKEND_ADDRESS: &str = "127.0.0.1:48327";

#[cfg(debug_assertions)]
const BACKEND_URL: &str = "http://127.0.0.1:5000";
#[cfg(not(debug_assertions))]
const BACKEND_URL: &str = "http://127.0.0.1:48327";

struct BackendProcess(Mutex<Option<Child>>);

fn backend_is_ready() -> bool {
    TcpStream::connect_timeout(
        &BACKEND_ADDRESS.parse().expect("valid backend address"),
        Duration::from_millis(500),
    )
    .is_ok()
}

fn backend_path(app: &AppHandle) -> Result<PathBuf, String> {
    let bundled_path = app
        .path()
        .resolve("backend/SignalTracker.Api.exe", BaseDirectory::Resource)
        .map_err(|error| format!("Could not resolve bundled backend: {error}"))?;

    if bundled_path.exists() {
        return Ok(bundled_path);
    }

    let development_path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .ok_or_else(|| "Tauri project directory was not found".to_string())?
        .join("backend-publish")
        .join("SignalTracker.Api.exe");

    Ok(development_path)
}

fn start_backend(app: &AppHandle, state: &BackendProcess) -> Result<(), String> {
    if backend_is_ready() {
        return Ok(());
    }

    let executable = backend_path(app)?;
    if !executable.exists() {
        return Err(format!("Backend executable was not found at {}", executable.display()));
    }

    let working_directory = executable
        .parent()
        .ok_or_else(|| "Backend working directory was not found".to_string())?;

    let mut command = Command::new(&executable);
    command
        .current_dir(working_directory)
        .env("ASPNETCORE_URLS", BACKEND_URL)
        .env("DOTNET_ENVIRONMENT", "Production")
        .env("ASPNETCORE_ENVIRONMENT", "Production")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    // The backend is a console app; keep its window hidden.
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }

    let child = command
        .spawn()
        .map_err(|error| format!("Could not start the local backend: {error}"))?;

    *state.0.lock().map_err(|_| "Backend state is unavailable".to_string())? = Some(child);

    let deadline = Instant::now() + Duration::from_secs(45);
    while Instant::now() < deadline {
        if backend_is_ready() {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(500));
    }

    Err(format!("The local backend did not become ready at {BACKEND_URL}"))
}

fn stop_backend(state: &BackendProcess) {
    if let Ok(mut process) = state.0.lock() {
        if let Some(mut child) = process.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .manage(BackendProcess(Mutex::new(None)))
        .setup(|app| {
            let state = app.state::<BackendProcess>();
            start_backend(app.handle(), &state).map_err(std::io::Error::other)?;
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while running Indoor Report")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::ExitRequested { .. } | tauri::RunEvent::Exit) {
                stop_backend(&app.state::<BackendProcess>());
            }
        });
}
