"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Copy,
  FileArchive,
  FileSpreadsheet,
  KeyRound,
  ArrowLeft,
  ArrowRight,
  Check,
  Loader2,
  Moon,
  Plus,
  Search,
  ShieldCheck,
  Sun,
  UploadCloud,
  X
} from "lucide-react";

const API_BASE_URL =
  process.env.NODE_ENV === "development" ? "http://127.0.0.1:5000" : "http://127.0.0.1:48327";
const MAX_FILE_SIZE = 500 * 1024 * 1024;
const MAX_FILE_SIZE_LABEL = "500 MB";
const REQUEST_TIMEOUT_MS = 7200000;

const REPORT_TYPE_OPTIONS = {
  excel: {
    label: "Excel",
    discoverPath: "/api/ExcelReport/DiscoverBands",
    generatePath: "/api/ExcelReport/GenerateFromZip",
    startGeneratePath: "/api/ExcelReport/StartGenerateFromZip",
    extension: "xlsx",
    fileField: "LogZips"
  }
};

const PLOT_INPUT_OPTIONS = [
  { value: "RSRP", label: "RSRP" },
  { value: "RSRQ", label: "RSRQ" },
  { value: "SINR", label: "SINR" },
  { value: "DL_THPT", label: "DL THPT" },
  { value: "UL_THPT", label: "UL THPT" },
  { value: "CI", label: "CI" },
  { value: "EARFCN", label: "EARFCN" },
  { value: "LTE_BLER", label: "LTE BLER" },
  { value: "PCI", label: "PCI" },
  { value: "NODEB_ID", label: "NodeB ID" },
  { value: "PUSCH_TX", label: "PUSCH TX" }
];

const toSafeArray = (value) => {
  if (Array.isArray(value)) return value;
  if (Array.isArray(value?.Data)) return value.Data;
  if (Array.isArray(value?.data)) return value.data;
  if (Array.isArray(value?.data?.Data)) return value.data.Data;
  if (Array.isArray(value?.AvailableBands)) return value.AvailableBands;
  if (Array.isArray(value?.availableBands)) return value.availableBands;
  if (Array.isArray(value?.BandsByFile)) return value.BandsByFile;
  if (Array.isArray(value?.bandsByFile)) return value.bandsByFile;
  return [];
};

const normalizeDiscoveredBands = (value) =>
  toSafeArray(value)
    .map((item) => {
      if (typeof item === "string") {
        const band = item.trim();
        return band ? { Band: band, Count: null, Percentage: null } : null;
      }

      const band = String(item?.Band ?? item?.band ?? "").trim();
      if (!band) return null;

      const count = Number(item?.Count ?? item?.count);
      const percentage = Number(item?.Percentage ?? item?.percentage);
      return {
        Band: band,
        Count: Number.isFinite(count) ? count : null,
        Percentage: Number.isFinite(percentage) ? percentage : null
      };
    })
    .filter(Boolean);

const normalizeBandsByFile = (value) =>
  toSafeArray(value)
    .map((fileItem, index) => {
      const availableBands = normalizeDiscoveredBands(
        fileItem?.AvailableBands ?? fileItem?.availableBands ?? fileItem?.bands
      );
      if (!availableBands.length) return null;

      return {
        FileIndex: Number(fileItem?.FileIndex ?? fileItem?.fileIndex ?? index),
        FileName:
          String(fileItem?.FileName ?? fileItem?.fileName ?? `File ${index + 1}`).trim() ||
          `File ${index + 1}`,
        TotalRows: Number(fileItem?.TotalRows ?? fileItem?.totalRows) || null,
        AvailableBands: availableBands
      };
    })
    .filter(Boolean)
    .sort((a, b) => a.FileIndex - b.FileIndex);

const attachFileBandCounts = (bandsList, bandsByFile) =>
  bandsList.map((band) => ({
    ...band,
    Files: bandsByFile
      .map((fileItem) => {
        const fileBand = fileItem.AvailableBands.find(
          (item) => item.Band.toLowerCase() === band.Band.toLowerCase()
        );
        if (!fileBand) return null;
        return {
          FileName: fileItem.FileName,
          Count: fileBand.Count,
          Percentage: fileBand.Percentage
        };
      })
      .filter(Boolean)
  }));

const getFileBandKey = (fileName, bandName) => `${fileName || "File"}::${bandName || ""}`;
const getReportFileKey = (file) => `${file?.name || "log"}-${file?.size || 0}-${file?.lastModified || 0}`;
const getDefaultSheetName = (file, index) => String(file?.name || "").replace(/\.zip$/i, "").trim() || `Floor ${index + 1}`;

const getSelectedBandsFromFileBands = (discoveredBands, selectedFileBands) => {
  const bands = new Set();
  discoveredBands.forEach((band) => {
    const files = Array.isArray(band.Files) ? band.Files : [];
    if (files.length === 0 && selectedFileBands[getFileBandKey("All Files", band.Band)]) {
      bands.add(band.Band);
      return;
    }

    files.forEach((fileBand) => {
      if (selectedFileBands[getFileBandKey(fileBand.FileName, band.Band)]) {
        bands.add(band.Band);
      }
    });
  });
  return Array.from(bands);
};

const parsePositiveInt = (value, fallback = 1) => {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
};

const buildReportFileGroups = (files, groupMap) =>
  files.map((file, index) => parsePositiveInt(groupMap[getReportFileKey(file)], index + 1));

const buildReportSheetNames = (files, sheetNameMap, reportMode, fileGroups = []) => {
  if (reportMode !== "combined") {
    return files.map((file) => String(sheetNameMap[getReportFileKey(file)] || "").trim()).filter(Boolean);
  }

  const grouped = new Map();
  files.forEach((file, index) => {
    const group = fileGroups[index] ?? index + 1;
    if (!grouped.has(group)) {
      grouped.set(group, String(sheetNameMap[getReportFileKey(file)] || "").trim());
    }
  });

  return Array.from(grouped.entries())
    .sort(([a], [b]) => a - b)
    .map(([, name]) => name)
    .filter(Boolean);
};

const formatBytes = (bytes) => {
  const value = Number(bytes);
  if (!Number.isFinite(value) || value <= 0) return "0 KB";
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / 1024 / 1024).toFixed(2)} MB`;
};

const makeApiUrl = (path) => `${API_BASE_URL}${path}`;

const sleep = (ms) => new Promise((resolve) => window.setTimeout(resolve, ms));

const isTransientNetworkError = (error) => {
  const message = String(error?.message || error || "").toLowerCase();
  return (
    error?.name === "TypeError" ||
    message.includes("network") ||
    message.includes("failed to fetch") ||
    message.includes("load failed")
  );
};

const parseErrorMessage = async (response) => {
  try {
    const data = await response.clone().json();
    return data?.message || data?.Message || data?.error || data?.Error || response.statusText;
  } catch {
    const text = await response.text().catch(() => "");
    return text || response.statusText;
  }
};

const requestWithTimeout = async (
  url,
  options = {},
  retries = process.env.NODE_ENV === "development" ? 8 : 2
) => {
  for (let attempt = 0; attempt <= retries; attempt += 1) {
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);

    try {
      return await fetch(url, {
        credentials: "include",
        ...options,
        signal: controller.signal
      });
    } catch (error) {
      const canRetry = attempt < retries && isTransientNetworkError(error);
      if (!canRetry) {
        throw new Error(
          `Local backend is not reachable at ${API_BASE_URL}. Start the desktop app backend and try again.`
        );
      }
      await sleep(process.env.NODE_ENV === "development" ? 1000 : 700 * (attempt + 1));
    } finally {
      window.clearTimeout(timer);
    }
  }
};

const postFormJson = async (path, formData) => {
  const response = await requestWithTimeout(makeApiUrl(path), {
    method: "POST",
    body: formData
  });

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response));
  }

  return response.json();
};

const postFormBlob = async (path, formData) => {
  const response = await requestWithTimeout(makeApiUrl(path), {
    method: "POST",
    body: formData
  });

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response));
  }

  return {
    blob: await response.blob(),
    contentDisposition: response.headers.get("content-disposition") || ""
  };
};

const getBlob = async (path) => {
  const response = await requestWithTimeout(makeApiUrl(path), {
    method: "GET"
  });

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response));
  }

  return {
    blob: await response.blob(),
    contentDisposition: response.headers.get("content-disposition") || ""
  };
};

const getJson = async (path) => {
  const response = await requestWithTimeout(makeApiUrl(path), {
    method: "GET"
  });

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response));
  }

  return response.json();
};

const postJson = async (path, payload) => {
  const response = await requestWithTimeout(makeApiUrl(path), {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    throw new Error(await parseErrorMessage(response));
  }

  return response.json();
};

const downloadBlob = async (blob, fileName) => {
  // Desktop app: ask the user where to save. Falls back to a browser download elsewhere.
  if (typeof window !== "undefined" && window.__TAURI_INTERNALS__) {
    const { save } = await import("@tauri-apps/plugin-dialog");
    const { writeFile } = await import("@tauri-apps/plugin-fs");
    const extension = fileName.split(".").pop();
    const path = await save({
      defaultPath: fileName,
      filters: extension ? [{ name: extension.toUpperCase(), extensions: [extension] }] : undefined
    });
    if (!path) return false;
    await writeFile(path, new Uint8Array(await blob.arrayBuffer()));
    return true;
  }

  const downloadUrl = window.URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = downloadUrl;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.URL.revokeObjectURL(downloadUrl);
  return true;
};

const INVALID_FILE_NAME_CHARS = new Set('<>:"/|?*'.split("").concat(String.fromCharCode(92)));

const sanitizeFileName = (value) =>
  Array.from(String(value || ""))
    .filter((char) => char.charCodeAt(0) > 31 && !INVALID_FILE_NAME_CHARS.has(char))
    .join("")
    .trim()
    .replace(/[. ]+$/, "");

const getHeaderFileName = (contentDisposition) => {
  const match = String(contentDisposition || "").match(/filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i);
  const raw = match?.[1] || match?.[2] || "";
  return raw ? decodeURIComponent(raw) : "";
};

const formatExpiry = (value) => {
  if (!value) return "No expiry";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Unknown expiry"
    : date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
};

export default function Home() {
  const inputRef = useRef(null);
  const [theme, setTheme] = useState("light");
  const [licenseStatus, setLicenseStatus] = useState(null);
  const [licenseKey, setLicenseKey] = useState("");
  const [licenseLoading, setLicenseLoading] = useState(true);
  const [licenseSaving, setLicenseSaving] = useState(false);
  const [licenseError, setLicenseError] = useState("");
  const [licenseMessage, setLicenseMessage] = useState("");
  const [showMachineId, setShowMachineId] = useState(false);
  const [licenseStep, setLicenseStep] = useState(1);
  const [isDragActive, setIsDragActive] = useState(false);
  const [reportFiles, setReportFiles] = useState([]);
  const [reportTitle, setReportTitle] = useState("");
  const [reportType, setReportType] = useState("excel");
  const [reportMode, setReportMode] = useState("separate");
  const [filterByImageName, setFilterByImageName] = useState(false);
  const [showSampleCount, setShowSampleCount] = useState(false);
  const [selectedPlotInputs, setSelectedPlotInputs] = useState(() => PLOT_INPUT_OPTIONS.map((option) => option.value));
  const [reportSheetNames, setReportSheetNames] = useState({});
  const [reportFileGroups, setReportFileGroups] = useState({});
  const [discoveredBands, setDiscoveredBands] = useState([]);
  const [selectedFileBands, setSelectedFileBands] = useState({});
  const [discoverLoading, setDiscoverLoading] = useState(false);
  const [generateLoading, setGenerateLoading] = useState(false);
  const [reportProgress, setReportProgress] = useState(null);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const reportOption = REPORT_TYPE_OPTIONS[reportType] || REPORT_TYPE_OPTIONS.excel;
  const isReportBusy = discoverLoading || generateLoading;
  const selectedBandNames = useMemo(
    () => getSelectedBandsFromFileBands(discoveredBands, selectedFileBands),
    [discoveredBands, selectedFileBands]
  );

  const resetDiscovery = useCallback(() => {
    setDiscoveredBands([]);
    setSelectedFileBands({});
    setError("");
    setMessage("");
  }, []);

  const refreshLicenseStatus = useCallback(async () => {
    setLicenseLoading(true);
    setLicenseError("");

    try {
      setLicenseStatus(await getJson("/api/license/status"));
    } catch (err) {
      setLicenseError(err?.message || "Could not read license status.");
      setLicenseStatus({ activated: false, machineId: "" });
    } finally {
      setLicenseLoading(false);
    }
  }, []);

  useEffect(() => {
    const savedTheme = window.localStorage.getItem("stracer.report.theme");
    const systemDark = window.matchMedia?.("(prefers-color-scheme: dark)")?.matches;
    setTheme(savedTheme || (systemDark ? "dark" : "light"));
  }, []);

  useEffect(() => {
    refreshLicenseStatus();
  }, [refreshLicenseStatus]);

  useEffect(() => {
    document.documentElement.classList.toggle("dark", theme === "dark");
    window.localStorage.setItem("stracer.report.theme", theme);
  }, [theme]);

  useEffect(() => {
    setReportSheetNames((prev) => {
      const next = {};
      reportFiles.forEach((file, index) => {
        const key = getReportFileKey(file);
        next[key] = prev[key] ?? getDefaultSheetName(file, index);
      });
      return next;
    });

    setReportFileGroups((prev) => {
      const next = {};
      reportFiles.forEach((file, index) => {
        const key = getReportFileKey(file);
        next[key] = String(prev[key] ?? index + 1);
      });
      return next;
    });
  }, [reportFiles]);

  const addFiles = useCallback(
    (fileList) => {
      const incoming = Array.from(fileList || []);
      const accepted = [];
      const rejected = [];

      incoming.forEach((file) => {
        const isZip = /\.zip$/i.test(file.name);
        if (!isZip) {
          rejected.push(`${file.name}: ZIP only`);
          return;
        }
        if (file.size > MAX_FILE_SIZE) {
          rejected.push(`${file.name}: max ${MAX_FILE_SIZE_LABEL}`);
          return;
        }
        accepted.push(file);
      });

      if (rejected.length) {
        setError(rejected.join(", "));
      }

      if (accepted.length) {
        setReportFiles((prev) => {
          const next = [...prev];
          accepted.forEach((file) => {
            const exists = next.some((item) => getReportFileKey(item) === getReportFileKey(file));
            if (!exists) next.push(file);
          });
          return next;
        });
        resetDiscovery();
      }
    },
    [resetDiscovery]
  );

  const handleDiscoverBands = async () => {
    if (!reportFiles.length) return;
    setDiscoverLoading(true);
    setError("");
    setMessage("");
    setDiscoveredBands([]);
    setSelectedFileBands({});

    try {
      const formData = new FormData();
      reportFiles.forEach((file) => formData.append(reportOption.fileField, file));
      formData.append("FilterByImageName", String(filterByImageName));

      const response = await postFormJson(reportOption.discoverPath, formData);
      const bandsList = normalizeDiscoveredBands(
        response?.AvailableBands ?? response?.availableBands ?? response?.Data?.AvailableBands ?? response?.data?.availableBands ?? response
      );
      const bandsByFile = normalizeBandsByFile(
        response?.BandsByFile ?? response?.bandsByFile ?? response?.Data?.BandsByFile ?? response?.data?.bandsByFile
      );
      const displayBands = bandsByFile.length > 0 ? attachFileBandCounts(bandsList, bandsByFile) : bandsList;

      if (!displayBands.length) {
        setError("No bands found in the selected ZIP files.");
        return;
      }

      const nextSelected = {};
      displayBands.forEach((band) => {
        const files = Array.isArray(band.Files) ? band.Files : [];
        if (!files.length) {
          nextSelected[getFileBandKey("All Files", band.Band)] = true;
          return;
        }
        files.forEach((fileBand) => {
          nextSelected[getFileBandKey(fileBand.FileName, band.Band)] = true;
        });
      });

      setDiscoveredBands(displayBands);
      setSelectedFileBands(nextSelected);
      setMessage("Bands discovered.");
    } catch (err) {
      setError(err?.message || "Failed to discover bands.");
    } finally {
      setDiscoverLoading(false);
    }
  };

  const handleGenerateReport = async () => {
    if (!reportFiles.length) {
      setError("Add at least one ZIP file.");
      return;
    }

    setGenerateLoading(true);
    setReportProgress({ progress: 3, stage: "Preparing report..." });
    setError("");
    setMessage("");

    try {
      const formData = new FormData();
      reportFiles.forEach((file) => formData.append(reportOption.fileField, file));
      formData.append("Title", reportTitle || "");
      formData.append("ProjectName", reportTitle || "");
      formData.append("FilterByImageName", String(filterByImageName));
      formData.append("ShowSampleCount", String(showSampleCount));
      selectedPlotInputs.forEach((plotInput) => formData.append("PlotInputs", plotInput));

      if (reportType === "excel") {
        formData.append("ReportMode", reportMode);
        const fileGroups = reportMode === "combined" ? buildReportFileGroups(reportFiles, reportFileGroups) : [];
        const sheetNames = buildReportSheetNames(reportFiles, reportSheetNames, reportMode, fileGroups);
        if (sheetNames.length) formData.append("SheetNames", sheetNames.join(","));
        if (reportMode === "combined") formData.append("LogFileGroups", fileGroups.join(","));
      }

      if (discoveredBands.length > 0 && selectedBandNames.length === 0) {
        setError("Select at least one band.");
        setGenerateLoading(false);
        return;
      }

      if (selectedPlotInputs.length === 0) {
        setError("Select at least one KPI plot.");
        setGenerateLoading(false);
        return;
      }

      if (selectedBandNames.length) {
        formData.append("BandFilter", selectedBandNames.join(","));
        selectedBandNames.forEach((band) => formData.append("Bands", band));
      }

      const startResponse = await postFormJson(reportOption.startGeneratePath || reportOption.generatePath, formData);
      const jobId = startResponse?.JobId || startResponse?.jobId;
      if (!jobId) {
        throw new Error("Report job did not return a JobId.");
      }

      let progressData = null;
      for (;;) {
        await sleep(900);
        progressData = await getJson(`/api/ExcelReport/Progress/${encodeURIComponent(jobId)}`);
        const progress = Number(progressData?.Progress ?? progressData?.progress ?? 0);
        const stage = progressData?.Stage || progressData?.stage || "Generating report...";
        const isCompleted = Boolean(progressData?.IsCompleted ?? progressData?.isCompleted);
        const isFailed = Boolean(progressData?.IsFailed ?? progressData?.isFailed);
        const failure = progressData?.Error || progressData?.error;

        setReportProgress({
          progress: Number.isFinite(progress) ? Math.max(0, Math.min(100, progress)) : 0,
          stage
        });

        if (isFailed) {
          throw new Error(failure || "Report generation failed.");
        }

        if (isCompleted) break;
      }

      setReportProgress((prev) => ({ progress: Math.max(prev?.progress || 0, 98), stage: "Downloading report..." }));
      const downloadPath =
        progressData?.DownloadUrl || progressData?.downloadUrl || `/api/ExcelReport/Download/${encodeURIComponent(jobId)}`;
      const { blob, contentDisposition } = await getBlob(downloadPath);
      const safeBands = selectedBandNames.join("-") || "ALL";
      const safeBaseName =
        reportFiles.length === 1 ? reportFiles[0].name.replace(/\.zip$/i, "") : `${reportFiles.length}_logs`;
      const extension = reportOption.extension;
      const titleName = sanitizeFileName(reportTitle);
      const fileName = titleName
        ? `${titleName}.${extension}`
        : getHeaderFileName(contentDisposition) || `${safeBaseName}_${safeBands}_report.${extension}`;
      const saved = await downloadBlob(blob, fileName);
      setReportProgress({ progress: 100, stage: saved ? "Report saved." : "Save cancelled." });
      setMessage(saved ? `${reportOption.label} report saved.` : "Save cancelled.");
    } catch (err) {
      setError(err?.message || "Failed to generate report.");
    } finally {
      setGenerateLoading(false);
    }
  };

  const setAllFileBands = (checked) => {
    const next = {};
    discoveredBands.forEach((band) => {
      const files = Array.isArray(band.Files) ? band.Files : [];
      if (!files.length) {
        next[getFileBandKey("All Files", band.Band)] = checked;
        return;
      }
      files.forEach((fileBand) => {
        next[getFileBandKey(fileBand.FileName, band.Band)] = checked;
      });
    });
    setSelectedFileBands(next);
  };

  const setAllPlotInputs = (checked) => {
    setSelectedPlotInputs(checked ? PLOT_INPUT_OPTIONS.map((option) => option.value) : []);
  };

  const togglePlotInput = (value) => {
    setSelectedPlotInputs((prev) =>
      prev.includes(value) ? prev.filter((item) => item !== value) : [...prev, value]
    );
  };

  const handleActivateLicense = async (event) => {
    event.preventDefault();
    setLicenseSaving(true);
    setLicenseError("");
    setLicenseMessage("");

    try {
      const status = await postJson("/api/license/activate", { licenseKey });
      setLicenseStatus(status);
      setLicenseKey("");
      setLicenseMessage(`Activated for ${formatExpiry(status.expiresUtc)}.`);
    } catch (err) {
      setLicenseError(err?.message || "License activation failed.");
    } finally {
      setLicenseSaving(false);
    }
  };

  const copyMachineId = async () => {
    const machineId = licenseStatus?.machineId;
    if (!machineId) return;

    try {
      await navigator.clipboard.writeText(machineId);
      setLicenseMessage("Machine ID copied.");
    } catch {
      setLicenseError("Could not copy Machine ID.");
    }
  };

  if (licenseLoading || !licenseStatus?.activated) {
    const steps = ["Machine ID", "License Key"];
    return (
      <main className="relative grid min-h-screen place-items-center overflow-hidden bg-slate-100 px-4 py-8 text-slate-950 transition-colors duration-700 dark:bg-[#071016] dark:text-slate-50">
        <div className="motion-grid" />
        <div className="scanline" />

        <section className="glass-panel wizard-card relative z-10 w-full max-w-lg p-6 sm:p-8">
          <div className="wizard-head">
            <img className="app-mark-large" src="/toollogo.svg" alt="Indoor Report" />
            <h1 className="mt-4 text-2xl font-black leading-tight tracking-normal sm:text-3xl">
              Activate Indoor Report
            </h1>
          </div>

          <ol className="stepper" aria-label="Activation steps">
            {steps.map((label, index) => (
              <li
                key={label}
                className={`step ${index + 1 === licenseStep ? "step-active" : ""} ${index + 1 < licenseStep ? "step-done" : ""}`}
              >
                <span className="step-dot">{index + 1 < licenseStep ? <Check className="h-3.5 w-3.5" /> : index + 1}</span>
                <span className="step-label">{label}</span>
              </li>
            ))}
          </ol>

          {licenseStep === 1 ? (
            <div className="grid gap-4 animate-pop">
              <p className="text-sm font-semibold leading-6 text-slate-600 dark:text-slate-300">
                Copy this machine ID and send it to Vinfocom to receive your license key.
              </p>
              <div className="license-machine-row">
                <code>{licenseStatus?.machineId || "Waiting for backend..."}</code>
                <button className="icon-action" type="button" aria-label="Copy Machine ID" onClick={copyMachineId}>
                  <Copy className="h-4 w-4" />
                </button>
              </div>
              {(licenseError || licenseMessage) && (
                <div className={licenseError ? "notice notice-error animate-pop" : "notice animate-pop"}>
                  {licenseError || licenseMessage}
                </div>
              )}
              <button
                className="btn-primary flex items-center justify-center gap-2"
                type="button"
                disabled={!licenseStatus?.machineId}
                onClick={() => {
                  setLicenseError("");
                  setLicenseMessage("");
                  setLicenseStep(2);
                }}
              >
                Continue
                <ArrowRight className="h-4 w-4" />
              </button>
            </div>
          ) : (
            <form className="grid gap-4 animate-pop" onSubmit={handleActivateLicense}>
              <label className="field-label">
                <span>License Key</span>
                <textarea
                  className="field-input license-key-input"
                  value={licenseKey}
                  onChange={(event) => setLicenseKey(event.target.value)}
                  placeholder="Paste license key"
                />
              </label>

              {(licenseError || licenseMessage) && (
                <div className={licenseError ? "notice notice-error animate-pop" : "notice animate-pop"}>
                  {licenseError || licenseMessage}
                </div>
              )}

              <div className="flex gap-3">
                <button
                  className="btn-secondary flex items-center justify-center gap-2"
                  type="button"
                  onClick={() => {
                    setLicenseError("");
                    setLicenseStep(1);
                  }}
                >
                  <ArrowLeft className="h-4 w-4" />
                  Back
                </button>
                <button className="btn-primary flex flex-1 items-center justify-center gap-2" type="submit" disabled={licenseSaving || !licenseKey.trim()}>
                  <KeyRound className="h-4 w-4" />
                  {licenseSaving ? "Activating..." : "Activate License"}
                </button>
              </div>
            </form>
          )}
        </section>
      </main>
    );
  }

  return (
    <main className="relative min-h-screen overflow-hidden bg-slate-100 text-slate-950 transition-colors duration-700 dark:bg-[#071016] dark:text-slate-50">
      <div className="top-bar">
          <img className="brand-logo" src="/company-logo.svg" alt="Vinfocom" />
        </div>


      <div className="motion-grid" />
      <div className="scanline" />

      <section className="relative z-10 mx-auto flex w-full max-w-7xl flex-col gap-5 px-4 py-5 sm:px-6 lg:px-8">
       
        <header className="glass-panel animate-rise flex flex-col gap-5 p-5 sm:p-6 lg:flex-row lg:items-center lg:justify-between">
          <div className="min-w-0">
            <div className="mb-3 flex flex-wrap items-center gap-2">
              <span className="rounded-full border border-teal-300/40 bg-teal-500/10 px-3 py-1 text-xs font-black uppercase tracking-[0.24em] text-teal-800 shadow-sm transition-all duration-500 dark:text-teal-200">
                Licensed until {formatExpiry(licenseStatus.expiresUtc)}
              </span>
              <button className="machine-id-button" type="button" onClick={() => setShowMachineId(true)}>
                <KeyRound className="h-3.5 w-3.5" />
                Machine ID
              </button>
            </div>
            <h1 className="max-w-4xl text-4xl font-black leading-tight tracking-normal text-slate-950 transition-colors duration-500 dark:text-white sm:text-5xl">
              Indoor Report
            </h1>
            <p className="mt-3 max-w-2xl text-sm font-medium leading-6 text-slate-600 transition-colors duration-500 dark:text-slate-300 sm:text-base">
              Turn indoor survey ZIP logs into polished Excel reports with band discovery, floor grouping.     </p>
          </div>

          <div className="flex flex-col gap-3 sm:flex-row lg:items-center">
            <div className="stats-card">
              <span className="text-xs font-bold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">
                Files
              </span>
              <strong className="text-2xl font-black">{reportFiles.length}</strong>
            </div>
            <div className="stats-card">
              <span className="text-xs font-bold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">
                Bands
              </span>
              <strong className="text-2xl font-black">{selectedBandNames.length}</strong>
            </div>
            <button
              className="theme-toggle group"
              type="button"
              aria-label="Toggle theme"
              onClick={() => setTheme((value) => (value === "dark" ? "light" : "dark"))}
            >
              <span className="theme-toggle-track">
                <span className={theme === "dark" ? "theme-toggle-dot translate-x-7" : "theme-toggle-dot"} />
              </span>
              <span className="flex items-center gap-2 text-sm font-black">
                {theme === "dark" ? <Moon className="h-4 w-4" /> : <Sun className="h-4 w-4" />}
                {theme === "dark" ? "Dark" : "Light"}
              </span>
            </button>
          </div>
        </header>

        <section className="grid gap-5 lg:grid-cols-[minmax(0,1.06fr)_minmax(360px,0.94fr)]">
          <div className="glass-panel animate-rise-delay p-4 sm:p-5">
            <input
              ref={inputRef}
              type="file"
              accept=".zip,application/zip,application/x-zip-compressed"
              multiple
              hidden
              onChange={(event) => {
                addFiles(event.target.files);
                event.target.value = "";
              }}
            />

            {reportFiles.length === 0 ? (
              <div
                className={`upload-zone group ${isDragActive ? "upload-zone-active" : ""}`}
                onDragEnter={() => setIsDragActive(true)}
                onDragLeave={() => setIsDragActive(false)}
                onDragOver={(event) => event.preventDefault()}
                onDrop={(event) => {
                  event.preventDefault();
                  setIsDragActive(false);
                  addFiles(event.dataTransfer.files);
                }}
                onClick={() => inputRef.current?.click()}
                role="button"
                tabIndex={0}
              >
                <div className="upload-icon">
                  <UploadCloud className="h-7 w-7 text-teal-700 transition-all duration-500 group-hover:-translate-y-1 dark:text-cyan-200" />
                </div>
                <div className="text-center">
                  <strong className="block text-xl font-black text-slate-950 transition-colors duration-500 dark:text-white">
                    Upload log ZIP files
                  </strong>
                  <span className="mt-2 block text-sm font-semibold text-slate-500 transition-colors duration-500 dark:text-slate-400">
                    .zip, max {MAX_FILE_SIZE_LABEL} each
                  </span>
                </div>
              </div>
            ) : (
              <div
                className={`compact-upload ${isDragActive ? "compact-upload-active" : ""}`}
                onDragEnter={() => setIsDragActive(true)}
                onDragLeave={() => setIsDragActive(false)}
                onDragOver={(event) => event.preventDefault()}
                onDrop={(event) => {
                  event.preventDefault();
                  setIsDragActive(false);
                  addFiles(event.dataTransfer.files);
                }}
              >
                <div>
                  <span className="text-xs font-black uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">
                    Uploaded ZIP files
                  </span>
                  <strong className="mt-1 block text-sm font-black">{reportFiles.length} selected</strong>
                </div>
                <button className="add-file-button" type="button" aria-label="Add ZIP files" onClick={() => inputRef.current?.click()}>
                  <Plus className="h-5 w-5" />
                </button>
              </div>
            )}

            {reportFiles.length > 0 && (
              <div className="mt-4 grid gap-3">
                {reportFiles.map((file, index) => (
                  <div className="glass-row group flex items-center gap-3" key={getReportFileKey(file)}>
                    <button
                      className="icon-action"
                      aria-label={`Remove ${file.name}`}
                      onClick={() => {
                        setReportFiles((prev) => prev.filter((_, fileIndex) => fileIndex !== index));
                        resetDiscovery();
                      }}
                    >
                      <X className="h-4 w-4" />
                    </button>
                    <FileArchive className="h-5 w-5 flex-none text-teal-700 dark:text-cyan-200" />
                    <div className="min-w-0 flex-1">
                      <strong className="block truncate text-sm font-black" title={file.name}>
                        {file.name}
                      </strong>
                      <span className="text-xs font-semibold text-slate-500 transition-colors duration-500 dark:text-slate-400">
                        {formatBytes(file.size)}
                      </span>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>

          <div className="glass-panel animate-rise-delay-2 p-4 sm:p-5">
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="field-label">
                <span>Report Title</span>
                <input className="field-input" value={reportTitle} onChange={(event) => setReportTitle(event.target.value)} />
              </label>
              <label className="field-label">
                <span>Report Type</span>
                <select
                  className="field-input"
                  value={reportType}
                  onChange={(event) => {
                    setReportType(event.target.value);
                    setReportMode("separate");
                    resetDiscovery();
                  }}
                >
                  <option value="excel">Excel</option>
                </select>
              </label>
            </div>

            <div className="mt-4 grid gap-3 sm:grid-cols-2">
              {reportType === "excel" && (
                <div className="field-label sm:col-span-2">
                  <span>Report Mode</span>
                  <div className="mac-segment" role="tablist" aria-label="Report mode">
                    {[
                      ["separate", "Separate"],
                      ["combined", "Combined"]
                    ].map(([value, label]) => (
                      <button
                        key={value}
                        type="button"
                        role="tab"
                        aria-selected={reportMode === value}
                        className={reportMode === value ? "mac-segment-button mac-segment-button-active" : "mac-segment-button"}
                        onClick={() => setReportMode(value)}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                </div>
              )}
              <label className="glass-check">
                <input
                  type="checkbox"
                  checked={filterByImageName}
                  onChange={(event) => {
                    setFilterByImageName(event.target.checked);
                    resetDiscovery();
                  }}
                />
                <span>Filter by image name</span>
              </label>
              <label className="glass-check">
                <input
                  type="checkbox"
                  checked={showSampleCount}
                  onChange={(event) => setShowSampleCount(event.target.checked)}
                />
                <span>Show sample count</span>
              </label>
            </div>

            {reportType === "excel" && reportMode === "combined" && reportFiles.length > 0 && (
              <div className="mt-4 grid max-h-72 gap-3 overflow-auto pr-1">
                {reportFiles.map((file, index) => {
                  const key = getReportFileKey(file);
                  return (
                    <div className="glass-row grid gap-3 sm:grid-cols-[minmax(0,1fr)_160px_82px]" key={key}>
                      <div className="min-w-0">
                        <span className="text-xs font-black uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">
                          ZIP File
                        </span>
                        <strong className="mt-1 block truncate text-sm font-black" title={file.name}>
                          {file.name}
                        </strong>
                      </div>
                      <label className="field-label">
                        <span>Sheet Name</span>
                        <input
                          className="field-input"
                          value={reportSheetNames[key] || ""}
                          onChange={(event) =>
                            setReportSheetNames((prev) => ({ ...prev, [key]: event.target.value }))
                          }
                          placeholder={`Floor ${index + 1}`}
                        />
                      </label>
                      <label className="field-label">
                        <span>Group</span>
                        <input
                          className="field-input"
                          value={reportFileGroups[key] ?? String(index + 1)}
                          onChange={(event) =>
                            setReportFileGroups((prev) => ({ ...prev, [key]: event.target.value }))
                          }
                        />
                      </label>
                    </div>
                  );
                })}
              </div>
            )}

            {reportType === "excel" && (
              <div className="mt-4">
                <div className="mb-3 flex items-center justify-between gap-3">
                  <label className="field-label min-w-0">
                    <span>KPI Plots</span>
                    <strong className="text-sm font-black">{selectedPlotInputs.length} selected</strong>
                  </label>
                  <div className="flex gap-2">
                    <button className="btn-ghost" type="button" onClick={() => setAllPlotInputs(true)}>
                      Select all
                    </button>
                    <button className="btn-ghost" type="button" onClick={() => setAllPlotInputs(false)}>
                      Clear
                    </button>
                  </div>
                </div>
                <div className="grid max-h-48 gap-2 overflow-auto pr-1 sm:grid-cols-2">
                  {PLOT_INPUT_OPTIONS.map((option) => (
                    <label className="glass-check min-w-0" key={option.value}>
                      <input
                        type="checkbox"
                        checked={selectedPlotInputs.includes(option.value)}
                        onChange={() => togglePlotInput(option.value)}
                      />
                      <span className="truncate" title={option.label}>
                        {option.label}
                      </span>
                    </label>
                  ))}
                </div>
              </div>
            )}
          </div>
        </section>

        {(error || message) && (
          <div className={error ? "notice notice-error animate-pop" : "notice animate-pop"}>{error || message}</div>
        )}

        {generateLoading && reportProgress && (
          <section className="glass-panel animate-pop p-4 sm:p-5">
            <div className="mb-3 flex items-center justify-between gap-3">
              <strong className="text-sm font-black">{reportProgress.stage}</strong>
              <span className="text-sm font-black text-teal-700 dark:text-cyan-200">
                {Math.round(reportProgress.progress)}%
              </span>
            </div>
            <div className="h-3 overflow-hidden rounded-full border border-white/40 bg-white/40 shadow-inner dark:border-white/10 dark:bg-white/10">
              <div
                className="h-full rounded-full bg-teal-600 transition-all duration-500 dark:bg-cyan-300"
                style={{ width: `${Math.max(3, Math.min(100, reportProgress.progress))}%` }}
              />
            </div>
          </section>
        )}

        <section className="grid gap-3 sm:grid-cols-2">
          <button
            className="btn-secondary flex items-center justify-center gap-2"
            onClick={handleDiscoverBands}
            disabled={isReportBusy || reportFiles.length === 0}
          >
            {discoverLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            {discoverLoading ? "Discovering bands..." : `Discover Bands for ${reportOption.label}`}
          </button>
          <button
            className="btn-primary flex items-center justify-center gap-2"
            onClick={handleGenerateReport}
            disabled={isReportBusy || reportFiles.length === 0}
          >
            {generateLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileSpreadsheet className="h-4 w-4" />}
            {generateLoading ? "Generating report..." : `Generate ${reportOption.label} Report`}
          </button>
        </section>

        {discoveredBands.length > 0 && (
          <section className="glass-panel animate-rise p-4 sm:p-5">
            <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <h2 className="text-xl font-black">Discovered File Bands</h2>
                <span className="text-sm font-bold text-slate-500 transition-colors duration-500 dark:text-slate-400">
                  {selectedBandNames.length} selected
                </span>
              </div>
              <div className="flex gap-2">
                <button className="btn-ghost" onClick={() => setAllFileBands(true)}>
                  Select all
                </button>
                <button className="btn-ghost" onClick={() => setAllFileBands(false)}>
                  Clear
                </button>
              </div>
            </div>

            <div className="grid max-h-[420px] gap-3 overflow-auto pr-1 md:grid-cols-2">
              {discoveredBands.map((band, index) => {
                const files =
                  Array.isArray(band.Files) && band.Files.length > 0
                    ? band.Files
                    : [{ FileName: "All Files", Count: band.Count, Percentage: band.Percentage }];

                return (
                  <div className="band-card group" key={`${band.Band}-${index}`}>
                    <div className="flex items-start justify-between gap-3">
                      <strong className="text-base font-black">{band.Band}</strong>
                      <span className="rounded-full border border-white/50 bg-white/30 px-2 py-1 text-xs font-black text-slate-600 backdrop-blur transition-all duration-500 dark:border-white/10 dark:bg-white/10 dark:text-slate-300">
                        {band.Count !== null ? `${band.Count} rows` : "Band"}
                        {band.Percentage !== null ? ` ${band.Percentage}%` : ""}
                      </span>
                    </div>
                    <div className="mt-3 grid gap-2">
                      {files.map((fileBand, fileIndex) => {
                        const key = getFileBandKey(fileBand.FileName, band.Band);
                        return (
                          <label className="glass-check min-w-0" key={`${key}-${fileIndex}`}>
                            <input
                              type="checkbox"
                              checked={Boolean(selectedFileBands[key])}
                              onChange={() =>
                                setSelectedFileBands((prev) => ({
                                  ...prev,
                                  [key]: !prev[key]
                                }))
                              }
                            />
                            <span className="truncate" title={fileBand.FileName}>
                              {fileBand.FileName}: {fileBand.Count ?? 0}
                              {fileBand.Percentage !== null ? ` (${fileBand.Percentage}%)` : ""}
                            </span>
                          </label>
                        );
                      })}
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
        )}
      </section>

      {showMachineId && (
        <div className="modal-shell animate-pop" role="dialog" aria-modal="true" aria-label="Machine ID">
          <div className="modal-backdrop" onClick={() => setShowMachineId(false)} />
          <section className="modal-panel glass-panel">
            <div className="flex items-start justify-between gap-4">
              <div>
                <h2 className="text-xl font-black">Machine ID</h2>
                <p className="mt-1 text-sm font-semibold text-slate-600 dark:text-slate-300">
                  Use this ID when issuing or renewing an Indoor Report license.
                </p>
              </div>
              <button className="icon-action" type="button" aria-label="Close" onClick={() => setShowMachineId(false)}>
                <X className="h-4 w-4" />
              </button>
            </div>

            <div className="mt-4 license-machine-row">
              <code>{licenseStatus?.machineId || "Unavailable"}</code>
              <button className="icon-action" type="button" aria-label="Copy Machine ID" onClick={copyMachineId}>
                <Copy className="h-4 w-4" />
              </button>
            </div>

            <button className="btn-primary mt-4 w-full" type="button" onClick={copyMachineId}>
              Copy Machine ID
            </button>
          </section>
        </div>
      )}
    </main>
  );
}
