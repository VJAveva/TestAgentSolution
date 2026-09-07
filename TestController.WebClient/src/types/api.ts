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
  skip?: boolean;
  skipReason?: string | null;
  comment?: string | null;
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
  skip?: boolean;
  skipReason?: string | null;
  comment?: string | null;
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

// ── Build Report Card (matches BuildReportCardController projection) ──────────

export type ReportSeverity = 'Pass' | 'Info' | 'Warn' | 'Fail';
export type PsrOutcome = 'Passed' | 'PassedWithWarnings' | 'Failed' | 'Pending';
export type FailurePatternKind =
  | 'Regression'
  | 'New'
  | 'Flaky'
  | 'Chronic'
  | 'Cascading'
  | 'Resolved'
  | 'Unknown';

export interface CiResult {
  name: string;
  total: number;
  passed: number;
  failed: number;
  skipped: number;
  passRate: number;
  severity: ReportSeverity;
}

export interface AgentResult {
  useCase: string;
  agentName: string;
  ci: string;
  passed: number;
  failed: number;
  skipped: number;
  total: number;
  durationLabel: string;
  passRate: number;
  severity: ReportSeverity;
}

export interface PsrResult {
  name: string;
  scenario: string;
  outcome: PsrOutcome;
  durationLabel: string;
  tags: number;
  throughput: string;
  errors: number;
  warnings: number;
  errorDetail?: string;
}

export interface FailureEntry {
  testName: string;
  ci: string;
  failedOnAgents: string[];
  pattern: FailurePatternKind;
  patternLabel: string;
  owner: string;
  firstSeen: string;
}

export interface TrendPoint {
  label: string;
  passRate: number;
  isCurrent: boolean;
}

export interface BuildGradeResult {
  letter: string;
  score: number;
  basePassRate: number;
  verdict: string;
  breakdownLines: string[];
  severity: ReportSeverity;
}

export interface BuildReportCard {
  buildNumber: string;
  generatedUtc: string;
  triggeredBy: string;
  startedUtc?: string;
  completedUtc?: string;
  durationLabel: string;
  hasData: boolean;
  grade: BuildGradeResult;
  totalTests: number;
  passedTests: number;
  failedTests: number;
  skippedTests: number;
  passRate: number;
  regressionCount: number;
  flakyCount: number;
  psrErrorCount: number;
  deltaVsLast?: number;
  psrPassCount: number;
  psrTotalCount: number;
  cis: CiResult[];
  agents: AgentResult[];
  psrs: PsrResult[];
  failures: FailureEntry[];
  trend: TrendPoint[];
}

// ── Regression tab / CIRP (matches ImpactController projections) ──────────────
// Backed by a mock provider until real Azure DevOps ingest lands — see
// docs/AzureIntegration/FEATURE-ARCHITECTURE.md. Shapes are the real v2 contract.

export type RegressionCategoryKind = 'Runtime' | 'Config' | 'Both' | 'Unclassified';
export type RegressionEvidenceKind = 'Assumed' | 'Declared' | 'Observed';
export type RegressionScopeKind = 'Build' | 'Weekly' | 'Custom' | 'Release';
export type RegressionWorkItemKind = 'Ims' | 'Bug' | 'Story' | 'Feature' | 'Other';
export type RegressionChangeKind = 'PullRequest' | 'Automated' | 'Commit';

export interface RegressionWorkItemRef {
  id: number;
  kind: RegressionWorkItemKind;
  title: string;
  url?: string;
  createdUtc?: string;
  workItemType?: string;
}

export interface RegressionChangeRef {
  changeId: string;
  summary: string;
  observedUtc: string;
  filePaths: string[];
  workItems: RegressionWorkItemRef[];
  kind: RegressionChangeKind;
  url?: string;
}

export interface RegressionSuiteRef {
  suiteId: string;
  isLinked: boolean;
  url?: string;
  evidence: RegressionEvidenceKind;
  title?: string;
}

export interface SubsystemRow {
  component: string;
  subsystem: string;
  category: RegressionCategoryKind;
  categoryConfidence: RegressionEvidenceKind;
  filesModified: string[];
  totalFilesModified: number;
  changes: RegressionChangeRef[];
  riskTier: string;
  automatedSuites: RegressionSuiteRef[];
  manualSuites: RegressionSuiteRef[];
  estimatedMinutes: number;
  isEstimate: boolean;
  regressionAreas?: string[];
  useCases?: string[];
  buildNumber?: string;
  buildFinishedUtc?: string;
  buildResult?: string;
  latestSuccessfulBuild?: string;
  latestSuccessfulBuildUrl?: string;
  repository?: string;
  repositoryUrl?: string;
  defaultBranch?: string;
  solutionNames?: string[];
}

export interface RegressionSummary {
  scopeLabel: string;
  rangeText: string;
  changeCount: number;
  subsystemCount: number;
  fileCount: number;
  weeklyActivity: number[];
}

export interface ConsolidatedImpact {
  summary: RegressionSummary;
  rows: SubsystemRow[];
}

export interface RegressionPlanColumn {
  subsystems: number;
  automatedSuites: number;
  manualSuites: number;
  gaps: number;
  estimatedMinutes: number;
}

export interface RegressionScope {
  runtime: RegressionPlanColumn;
  config: RegressionPlanColumn;
  unmappedSubsystems: string[];
  parallelAgentCount: number;
}

export interface RegressionSyncStatus {
  state: string;
  lastSyncUtc?: string;
  mapVersion: string;
  unresolvedRepositories: string[];
}

export interface ImpactIndexHealth {
  status: 'Ready' | 'Missing' | 'Empty' | 'Corrupt' | 'Stale' | string;
  healthy: boolean;
  message: string;
  documentCount: number;
  lastBuiltUtc?: string;
}

export interface RegressionConnectionInfo {
  enabled: boolean;
  mode: string;
  organization: string;
  project: string;
  omiProject: string;
  authMode: string;
  credentialSource: string;
  credentialConfigured: boolean;
}

export interface RegressionComponentRef {
  name: string;
  definitionId: number;
}

export interface RegressionBuildRef {
  buildId: number;
  buildNumber: string;
  result: string;
  finishedUtc?: string;
  display: string;
}

export interface ChurnSummary {
  headline: string;
  highlights: string[];
  narrative: string;
}

/** One Test Case matched to an impacted component by the impact-mapping engine (grid row-expand + churn Excel). */
export interface ImpactedTestCaseMatch {
  impactedArea: string;
  testCaseId: number;
  testCaseTitle: string;
  description?: string;
  testCaseUrl?: string;
  parentFeatureId: number;
  matchType: string;
  confidencePercent: number;
  matchReason: string;
  linkedWorkItems?: RegressionWorkItemRef[] | null;
}

/** A functional test recommended for a change; `relevant` when its name matches the change's themes. */
export interface RecommendedTest {
  name: string;
  relevant: boolean;
}

/** Row-expand payload: engine matches plus the offline change summary + recommended tests fallback. */
export interface ImpactedComponentAnalysis {
  matches: ImpactedTestCaseMatch[];
  changeSummary: string;
  recommendedTests: RecommendedTest[];
  indexHealthMessage?: string | null;
}

