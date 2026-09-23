export interface TimeWindow {
    from: string;
    to: string;
}
export interface DayConfig {
    limit_min: number;
    windows: TimeWindow[];
}
export type DaysConfig = Record<string, DayConfig>;
export interface ServerConfig {
    version: number;
    force_lock: boolean;
    idle_threshold_sec: number;
    count_only_active: boolean;
    days: DaysConfig;
}
export interface UsageEntry {
    active_min: number;
    mode: "limit" | "window";
    quota: string;
}
export interface DeviceFile {
    device_id: string;
    name: string;
    token: string;
    last_seen: string | null;
    config: ServerConfig;
    usage: Record<string, UsageEntry>;
}
export declare const WEEKDAYS: readonly ["1", "2", "3", "4", "5", "6", "7"];
export declare function defaultDays(): DaysConfig;
export declare function defaultConfig(): ServerConfig;
export declare function validateDays(days: unknown): string | null;
export declare function normalizeHostname(hostname: string): string;
