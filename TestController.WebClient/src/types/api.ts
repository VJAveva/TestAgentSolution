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
  tag: string;
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

// ?? Build detail types (from /api/results/builds/{id}/detail) ????????

export interface BuildDetailTest {
  testName: string;
  className: string;
  useCase: string;
  outcome: string;
  duration: string;
  durationText: string;
  errorMessage?: string;
  stackTrace?: string;
  debugTrace?: string;
  stdOut?: string;
  trxFile: string;
  steps?: TestStepDto[];
}

export interface TestStepDto {
  stepName: string;
  outcome: string;
  duration: string;
  stdOut?: string;
  errorMessage?: string;
}

export interface BuildDetailUseCaseSummary {
  useCaseName: string;
  total: number;
  passed: number;
  failed: number;
  timeout: number;
  notExecuted: number;
  passRate: number;
  duration: string;
}

export interface BuildDetailResponse {
  buildNumber: string;
  earliestRun?: string;
  latestRun?: string;
  totalDuration: string;
  totalTests: number;
  passedTests: number;
  failedTests: number;
  timeoutTests: number;
  notExecutedTests: number;
  passRate: number;
  health: string;
  useCases: BuildDetailUseCaseSummary[];
  filteredCount: number;
  tests: BuildDetailTest[];
  filters: { outcome?: string; useCase?: string; search?: string };
}

export interface TrendReport {
  builds: BuildTrendEntry[];
  weeklySummaries: PeriodSummary[];
  monthlySummaries: PeriodSummary[];
  goodThreshold: number;
  warningThreshold: number;
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

export interface SessionInfo {
  sessionId: string;
  watchItemTag: string;
  eventType: string;
  state: string;
  startedUtc: string;
  totalActions: number;
  completedActions: number;
  passedActions: number;
  failedActions: number;
  progressPercent: number;
}

export interface SessionsResponse {
  activeCount: number;
  hasActive: boolean;
  sessions: SessionInfo[];
}

export interface LogEntry {
  message: string;
  agent?: string;
  sessionId?: string;
  timestamp: string;
  kind?: 'stdout' | 'stderr';
  severity?: 'info' | 'success' | 'warning' | 'error';
  /** Component/category that emitted the entry (e.g. "Dispatch", "Action"). Distinct from {@link agent}. */
  component?: string;
  /** Run/execution id that groups every entry of one pipeline execution end-to-end. */
  runId?: string;
  /** Specific action/step identity within the pipeline (resolved tag). */
  action?: string;
  /** Exception / error detail captured as a single field (rendered expandable). */
  exception?: string;
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

// ── Failure Analysis models ─────────────────────────────────────────

export type FailurePattern = 'None' | 'SystemicRegression' | 'CascadingFailures' | 'FlakyTest' | 'ChronicFailure' | 'Resolved' | 'NewFailure';

export interface FailureSignature {
  buildName: string;
  failedStepIndex: number;
  failedStepName: string;
  errorType: string;
  normalizedMessage: string;
  topStackFrame: string;
  agent: string;
  duration: string;
}

export interface TestExecutionRecord {
  buildName: string;
  buildDate: string;
  outcome: string;
  duration: string;
  errorMessage: string;
  agent: string;
}

export interface FailureAnalysisReport {
  testCaseName: string;
  pattern: FailurePattern;
  verdict: string;
  confidence: number;
  consecutiveFailures: number;
  totalBuildsAnalyzed: number;
  totalFailures: number;
  flakeRate: number;
  lastPassBuild?: string;
  firstFailBuild?: string;
  allSignaturesMatch: boolean;
  suggestedAction: string;
  signatures: FailureSignature[];
  history: TestExecutionRecord[];
}

// ── Execution Log models ────────────────────────────────────────────

export interface MergedLogLine {
  timestamp: string;
  source: string;
  severity: string;
  message: string;
}

export interface ExecutionLogReport {
  buildName: string;
  testCaseName: string;
  outcome: string;
  agent: string;
  startTime: string;
  endTime: string;
  duration: string;
  failedStepIndex: number;
  errorMessage: string;
  stackTrace: string;
  mergedTimeline: MergedLogLine[];
}
