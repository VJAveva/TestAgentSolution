// ?? WatchList models (matches TestControllerGrpc.Models) ????????????

export interface WatchListConfig {
  watchItems: WatchItemConfig[];
  templates: TemplateConfig[];
  filePath: string;
}

export interface WatchItemConfig {
  tag: string;
  path: string;
  filter: string;
  events: EventConfig[];
  isEnabled: boolean;
  buildNumberField: string;
  dropLocationField: string;
  lastBuildNumber?: string;
  lastDropLocation?: string;
}

export interface EventConfig {
  type: string;
  executionType: ExecutionMode;
  children: ActionNode[];
}

export type ExecutionMode = 'Sequential' | 'Parallel';
export type ActionType = 'RunCommand' | 'RunRemoteCommand' | 'SendMail';

export type ActionNode =
  | ActionGroupConfig
  | ActionConfig
  | InitializeConfig
  | RefConfig;

export interface ActionGroupConfig {
  nodeType: 'ActionGroup';
  tag: string;
  executionType: ExecutionMode;
  failAndContinue: boolean;
  children: ActionNode[];
}

export interface ActionConfig {
  nodeType: 'Action';
  type: ActionType;
  agentName: string;
  command: string;
  parameters: string;
  timeout: number;
  pollInterval: number;
  failAndContinue: boolean;
  isReboot: boolean;
  order: string;
  userName: string;
  password: string;
  from: string;
  to: string;
  title: string;
  body: string;
  attachment: string;
  embed: string;
  largeFilesShare: string;
}

export interface InitializeConfig {
  nodeType: 'Initialize';
  tag: string;
  parameterFile: string;
}

export interface RefConfig {
  nodeType: 'Ref';
  templateID: string;
}

export interface TemplateConfig {
  id: string;
  children: ActionNode[];
}

// ?? Build results models (matches TrxModels) ????????????????????????

export type HealthStatus = 'Unknown' | 'Good' | 'Warning' | 'Bad';

export interface BuildSummary {
  buildNumber: string;
  modified: string;
  totalTests: number;
  passedTests: number;
  failedTests: number;
  timeoutTests: number;
  passRate: number;
  health: string;
}

export interface BuildNode {
  buildNumber: string;
  rootPath: string;
  earliestRun?: string;
  latestRun?: string;
  totalTests: number;
  passedTests: number;
  failedTests: number;
  timeoutTests: number;
  notExecutedTests: number;
  passRate: number;
  health: HealthStatus;
  useCases: UseCaseNode[];
  allFailedTests: TestResult[];
}

export interface UseCaseNode {
  useCaseName: string;
  duration: string;
  total: number;
  passed: number;
  failed: number;
  timeout: number;
  notExecuted: number;
  passRate: number;
  testResults: TestResult[];
  failedTests: TestResult[];
}

export interface TestResult {
  testName: string;
  className: string;
  outcome: string;
  duration: string;
  errorMessage?: string;
  stackTrace?: string;
  stdOut?: string;
  trxFileName: string;
  useCaseName: string;
}

export interface TrendReport {
  builds: BuildTrendEntry[];
  weeklySummaries: PeriodSummary[];
  monthlySummaries: PeriodSummary[];
}

export interface BuildTrendEntry {
  buildNumber: string;
  date: string;
  totalTests: number;
  passedTests: number;
  failedTests: number;
  timeoutTests: number;
  passRate: number;
  health: HealthStatus;
}

export interface PeriodSummary {
  period: string;
  buildCount: number;
  totalTests: number;
  totalPassed: number;
  totalFailed: number;
  avgPassRate: number;
}

export interface ConsecutiveFailureAlert {
  testName: string;
  useCaseName: string;
  priority: string;
  lastError: string;
  consecutiveFailCount: number;
  failedInBuilds: string[];
}

// ?? Agent models ????????????????????????????????????????????????????

export interface AgentInfo {
  name: string;
  address: string;
  status: string;
  lastStatusDetail?: string;
  lastCheckedUtc?: string;
}

export interface DiagnosticStep {
  step: string;
  passed: boolean;
  detail: string;
}

// ?? Execution models ????????????????????????????????????????????????

export interface ExecutionStatus {
  isExecuting: boolean;
  activeCount: number;
}

export interface LogEntry {
  message: string;
  agent?: string;
  sessionId?: string;
  timestamp: string;
  kind?: 'stdout' | 'stderr';
  severity?: 'info' | 'success' | 'warning' | 'error';
}

// ?? Tree node (UI-side flattened model) ?????????????????????????????

export type NodeKind = 'WatchList' | 'WatchItem' | 'Event' | 'ActionGroup' | 'Action' | 'Initialize' | 'Ref' | 'TemplateList' | 'Template';
export type NodeStatus = 'Idle' | 'Running' | 'Success' | 'Failed';

export interface TreeNode {
  id: string;
  nodeKind: NodeKind;
  displayText: string;
  tag: string;
  executionStatus: NodeStatus;
  children: TreeNode[];
  isExpanded: boolean;
  depth: number;
  model?: WatchItemConfig | EventConfig | ActionNode | TemplateConfig;
}
