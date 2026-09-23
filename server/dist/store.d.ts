import { DeviceFile, ServerConfig } from "./types.js";
export declare function dataDir(): string;
export declare function distDir(): string;
export declare function ensureDir(dir: string): Promise<void>;
/** Atomic write: tmp + rename. */
export declare function saveDevice(dir: string, device: DeviceFile): Promise<void>;
export declare function loadDevice(dir: string, deviceId: string): Promise<DeviceFile | null>;
export declare function listDevices(dir: string): Promise<DeviceFile[]>;
export interface RegisterResult {
    created: boolean;
    device: DeviceFile;
}
/** Auto-register: create default (no limits) device on first contact. */
export declare function registerDevice(dir: string, hostname: string, opts?: {
    allowAutoRegister?: boolean;
}): Promise<RegisterResult>;
export declare function snapshotQuota(day: {
    limit_min: number;
    windows: {
        from: string;
        to: string;
    }[];
}, mode: "limit" | "window"): string;
export declare function weekdayOf(dateStr: string): string;
export declare function recordHeartbeat(dir: string, device: DeviceFile, entry: {
    date: string;
    active_min: number;
    locked: boolean;
}): Promise<DeviceFile>;
export declare function applyConfigUpdate(device: DeviceFile, patch: Partial<ServerConfig>): {
    error: string | null;
};
