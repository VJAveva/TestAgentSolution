# Documentation index

One line per document, grouped by folder. Status:
**Current** = checked against the code on its `last-verified` date (feature READMEs);
**Draft** = not re-checked against the code since it was written;
**Archived** = superseded or closed, kept for history.

## Start here

- New to the controller: [getting-started/ONBOARDING.md](getting-started/ONBOARDING.md)
- Setting up a controller for another team: [getting-started/Team-Onboarding.md](getting-started/Team-Onboarding.md)
- How the system fits together: [architecture/README.md](architecture/README.md)
- Every feature has a README with its code, tests, config and known gaps: see **Features** below.
- Security Framework (REST authentication): [deployment guide](features/SecurityFramework/DEPLOYMENT-GUIDE.md),
  [migration guide](features/SecurityFramework/MIGRATION-GUIDE.md),
  [troubleshooting](features/SecurityFramework/TROUBLESHOOTING.md).
- Check links after editing docs: `.\tools\Check-DocLinks.ps1`

Notes:
- `docs/impact/*.json` and `*.xml` are data files the code reads; they stay in `docs/impact/`.
- `docs/FIRFailedTest/Fir/` holds FIR test fixtures read by the tests; it stays where it is.
- `docs/PreFlightCheck/` is git-ignored on purpose (it holds a real pipeline config) and is not part of the repo.

## Getting started

- [Onboarding: First Run on the TestController WebApi](getting-started/ONBOARDING.md) - Draft
- [Team onboarding: run your own TestController](getting-started/Team-Onboarding.md) - Draft

## Architecture

- [TestAgentSolution — Architecture & System Design](architecture/ARCHITECTURE.md) - Draft
- [TestAgentSolution — Architecture Design Document](architecture/ARCHITECTURE_DESIGN_DOCUMENT.md) - Draft
- [TestAgentSolution — System Design & Architecture Diagrams](architecture/ARCHITECTURE-DIAGRAMS.md) - Draft
- [TestAgentSolution — Architecture & System Design Document](architecture/ARCHITECTURE-overview.md) - Draft
- [TestAgentSolution — Complete Design](architecture/COMPLETE_DESIGN.md) - Draft
- [Conventions — TestAgentSolution](architecture/CONVENTIONS.md) - Draft
- [Current State — TestAgentSolution](architecture/CURRENT_STATE.md) - Draft
- [Architecture & Codebase Guide — Feature Planning Reference](architecture/FEATURE_PLANNING_GUIDE.md) - Draft
- [Pipeline Trigger Sequence](architecture/pipeline-trigger-sequence.md) - Draft
- [Architecture — Start Here](architecture/README.md) - Draft
- [TestAgentSolution — System Design](architecture/SYSTEM_DESIGN.md) - Draft
- [TestAgentSolution Architecture](<architecture/TestAgentSolution Architecture.html>) - Draft
- [WebApi Database Decoupling — Architecture Report](architecture/WebApi-DB-Decoupling.md) - Draft

## Operations

- [Patch-AgentFleet — Setup Guide](operations/AGENT_FLEET_PATCH_SETUP.md) - Draft
- [CI / Local Test Commands](operations/CI-COMMANDS.md) - Draft
- [Operations Runbook](operations/RUNBOOK.md) - Draft

## Features

### AgentDiagnostics

- [TestAgent Diagnostics (log viewer)](features/AgentDiagnostics/README.md) - README.md - Current
- [TestAgent Diagnostics — Build Spec](features/AgentDiagnostics/TestAgent_Diagnostics_BuildSpec.md) - TestAgent_Diagnostics_BuildSpec.md - Draft

### APITest

- [API workflow test framework](features/APITest/README.md) - README.md - Current
- [API Workflow Test Framework — Implementation Specification](features/APITest/ApiTestFramework_Implementation_Spec.md) - ApiTestFramework_Implementation_Spec.md - Draft

### AzureIntegration

- [Azure DevOps integration (shared by Code Churn and Impact)](features/AzureIntegration/README.md) - README.md - Current
- [Feature Architecture — Change Impact & Regression Planning](features/AzureIntegration/FEATURE-ARCHITECTURE.md) - FEATURE-ARCHITECTURE.md - Draft
- [Impacted Test Mapping — Copilot Build Guide](features/AzureIntegration/ImpactMapping-Copilot-BuildGuide.md) - ImpactMapping-Copilot-BuildGuide.md - Draft
- [AppServer — Complete Project-Level Structure](features/AzureIntegration/ProjectlevelComponents.md) - ProjectlevelComponents.md - Draft

### BuildReportCard

- [Build Report Card](features/BuildReportCard/README.md) - README.md - Current
- [Build Report Card — Requirements & Implementation Spec](features/BuildReportCard/BuildReportCard_Requirements.md) - BuildReportCard_Requirements.md - Draft
- [Build Report Card — Mockup](features/BuildReportCard/mockups/BuildReportCard_Mockup.html) - mockups/BuildReportCard_Mockup.html - Draft

### ClientNodeExecution

- [Node-wise execution from the web client](features/ClientNodeExecution/README.md) - README.md - Current
- [WebClient — Node-Wise Execution](features/ClientNodeExecution/mockups/WebClient_NodeWise_Execution_Mockup.html) - mockups/WebClient_NodeWise_Execution_Mockup.html - Draft
- [Implementation Spec: WebClient Node-Wise Execution](features/ClientNodeExecution/WebClient_NodeWise_Execution_Spec.md) - WebClient_NodeWise_Execution_Spec.md - Draft

### CodeChurn

- [Code Churn (Regression tab)](features/CodeChurn/README.md) - README.md - Current
- [Connecting to Azure DevOps](features/CodeChurn/Azure-DevOps-Integration-Guide.md) - Azure-DevOps-Integration-Guide.md - Draft
- [Code churn header — compaction mockup](features/CodeChurn/mockups/CodeChurn-Header-Mockup.html) - mockups/CodeChurn-Header-Mockup.html - Draft
- [TestController - Regression](features/CodeChurn/mockups/regression-tab-mockup.html) - mockups/regression-tab-mockup.html - Draft
- [Regression - redesigned](features/CodeChurn/mockups/regression-tab-redesign.html) - mockups/regression-tab-redesign.html - Draft
- [Code Churn Ribbon — Redesign Mockup](features/CodeChurn/mockups/RibbonRedesign-CodeChurn-Mockup.html) - mockups/RibbonRedesign-CodeChurn-Mockup.html - Draft
- [WebClient churn toolbar — redesign](features/CodeChurn/mockups/WebClient-ChurnToolbar-Redesign.html) - mockups/WebClient-ChurnToolbar-Redesign.html - Draft
- [Code Churn Header Compaction — Copilot Prompt Pack](features/CodeChurn/prompts/CodeChurn-Header-Compaction-CopilotPrompts.md) - prompts/CodeChurn-Header-Compaction-CopilotPrompts.md - Draft
- [Copilot implementation prompts](features/CodeChurn/prompts/Copilot-Prompts-AdoIntegration.md) - prompts/Copilot-Prompts-AdoIntegration.md - Draft
- [Regression tab — UI implementation spec](features/CodeChurn/RegressionTab-UI-Spec.md) - RegressionTab-UI-Spec.md - Draft
- [WebClient Parity Architecture — Regression / ADO Feature](features/CodeChurn/WebClient-Parity-Architecture.md) - WebClient-Parity-Architecture.md - Draft

### Docs

- [Documentation tooling](features/Docs/README.md) - README.md - Current
- [Prompt — Restructure docs so they stay in line with the code](features/Docs/prompts/Prompt_Docs_Restructure.md) - prompts/Prompt_Docs_Restructure.md - Draft

### ExecutionDashboard

- [Execution dashboard and live log](features/ExecutionDashboard/README.md) - README.md - Current
- [Feature Spec: Populate Sessions Panel + Agent-Filtered Live Log](features/ExecutionDashboard/LiveExecutionLog_SessionsPanel_Spec.md) - LiveExecutionLog_SessionsPanel_Spec.md - Draft
- [execution_dashboard_mockup_v2 (2)](<features/ExecutionDashboard/mockups/execution_dashboard_mockup_v2 (2).html>) - mockups/execution_dashboard_mockup_v2 (2).html - Draft
- [Live Execution Log - Mockup](features/ExecutionDashboard/mockups/LiveExecutionLog_Mockup.html) - mockups/LiveExecutionLog_Mockup.html - Draft
- [Use Case Execution — Corrected Panel Mockup](features/ExecutionDashboard/mockups/UseCaseExecution_Fixed_Mockup.html) - mockups/UseCaseExecution_Fixed_Mockup.html - Draft

### FeatureToggles

- [Per-team feature toggles](features/FeatureToggles/README.md) - README.md - Current
- [Prompt — Per-team feature toggles (hide Results, Code Churn, etc.) + per-team Code Churn branches](features/FeatureToggles/prompts/Prompt_Team_Feature_Toggles.md) - prompts/Prompt_Team_Feature_Toggles.md - Draft

### FIR

- [FIR: First Information Report for failed tests](features/FIR/README.md) - README.md - Current
- [FIR — Implementation Plan](features/FIR/FIR_Implementation_Plan.md) - FIR_Implementation_Plan.md - Draft
- [Requirement: First Information Report (FIR) for test failures](features/FIR/FIR_Requirement.md) - FIR_Requirement.md - Draft
- [First Information Report](features/FIR/mockups/FIR_Results_Mockup.html) - mockups/FIR_Results_Mockup.html - Draft
- [Copilot Prompt — First Information Report (FIR)](features/FIR/prompts/FIR_Copilot_Prompt.md) - prompts/FIR_Copilot_Prompt.md - Draft
- [Task (parked): copy the test-data workbook back to the controller](features/FIR/TASK_CopyWorkbookToController.md) - TASK_CopyWorkbookToController.md - Draft

### FleetMaintenance

- [Fleet maintenance: revert, reboot, updates, golden image](features/FleetMaintenance/README.md) - README.md - Current
- [Requirement & Recommended Approach: On-Demand Agent Update Button](features/FleetMaintenance/AgentUpdateButton_Requirements.md) - AgentUpdateButton_Requirements.md - Draft
- [Fleet Update Orchestration — Findings & Design](features/FleetMaintenance/FleetUpdateOrchestration-Design.md) - FleetUpdateOrchestration-Design.md - Draft
- [Fleet update → snapshot: audit](features/FleetMaintenance/FLEET-UPDATE-SNAPSHOT.md) - FLEET-UPDATE-SNAPSHOT.md - Draft
- [Fleet Maintenance — UI Spec (R1–R24)](features/FleetMaintenance/mockups/FleetRevert-UI-Spec.html) - mockups/FleetRevert-UI-Spec.html - Draft
- [Build Spec: Windows Update Installer + Snapshot Wiring for Golden-Image Refresh](features/FleetMaintenance/NodeUpdateInstaller_Build_Spec.md) - NodeUpdateInstaller_Build_Spec.md - Draft
- [Hardening Audit & Plan: Agent Fleet Maintenance (the USP feature)](features/FleetMaintenance/prompts/FleetMaintenance_Hardening_Prompt.md) - prompts/FleetMaintenance_Hardening_Prompt.md - Draft
- [Fleet Maintenance — Workflow & Copilot Prompts](features/FleetMaintenance/prompts/FleetRevert-Workflow-and-CopilotPrompts.md) - prompts/FleetRevert-Workflow-and-CopilotPrompts.md - Draft

### GlobalVariables

- [Global Variables](features/GlobalVariables/README.md) - README.md - Current
- [Requirement: Global Variables (single-release build for all pipelines)](features/GlobalVariables/GlobalVariables_Requirement.md) - GlobalVariables_Requirement.md - Draft
- [Global Variables Dialog](features/GlobalVariables/mockups/GlobalVariables_Dialog_Mockup.html) - mockups/GlobalVariables_Dialog_Mockup.html - Draft
- [Copilot Prompt — Global Variables (single-release build for all pipelines)](features/GlobalVariables/prompts/GlobalVariables_Copilot_Prompt.md) - prompts/GlobalVariables_Copilot_Prompt.md - Draft

### Hardening

- [Hardening and audits](features/Hardening/README.md) - README.md - Current
- [Overall System Hardening & Sanitization Plan](features/Hardening/00-overall-system-hardening-plan.md) - 00-overall-system-hardening-plan.md - Draft
- [TestControllerGrpc Hardening Plan](features/Hardening/01-controller-grpc-hardening.md) - 01-controller-grpc-hardening.md - Draft
- [TestControllerGrpc Hardening — Systematic Implementation Plan](features/Hardening/01-controller-grpc-implementation-plan.md) - 01-controller-grpc-implementation-plan.md - Draft
- [TestAgentGrpc Hardening Plan](features/Hardening/02-agent-grpc-hardening.md) - 02-agent-grpc-hardening.md - Draft
- [TestController.WebApi Hardening Plan](features/Hardening/03-controller-webapi-hardening.md) - 03-controller-webapi-hardening.md - Draft
- [TestController.Api Hardening Plan](features/Hardening/04-controller-api-hardening.md) - 04-controller-api-hardening.md - Draft
- [TestControllerGrpc.Core Hardening Plan](features/Hardening/05-controller-core-hardening.md) - 05-controller-core-hardening.md - Draft
- [TestController.WebClient Hardening Plan](features/Hardening/06-webclient-hardening.md) - 06-webclient-hardening.md - Draft
- [TestAgentDisplay / TestController.Dashboard Hardening Plan](features/Hardening/07-agent-display-hardening.md) - 07-agent-display-hardening.md - Draft
- [08-tests-hardening](features/Hardening/08-tests-hardening.md) - 08-tests-hardening.md - Draft
- [Deployment & Operations Hardening Plan](features/Hardening/09-deployment-ops-hardening.md) - 09-deployment-ops-hardening.md - Draft
- [TestAgentSolution — Independent Codebase Audit](features/Hardening/AUDIT.md) - AUDIT.md - Draft
- [TestAgentSolution — Code Audit & Hardening Prompt Pack](features/Hardening/CodeAudit-Copilot-Prompts.md) - CodeAudit-Copilot-Prompts.md - Draft
- [Action Plan: Consolidate the 4 Proto Copies into 1 Shared File](features/Hardening/Fix_ProtoConsolidation_ActionPlan.md) - Fix_ProtoConsolidation_ActionPlan.md - Draft
- [Fleet Maintenance — Pre-Production Hardening Audit](features/Hardening/HARDENING.md) - HARDENING.md - Draft
- [TestAgentSolution — Hardening, Performance & Architecture Prompt Pack](features/Hardening/Hardening_Prompt_Pack.md) - Hardening_Prompt_Pack.md - Draft
- [Hardening Register (single source of truth)](features/Hardening/REGISTER.md) - REGISTER.md - Draft
- [SECURITY-GAPS.md — Requirement 11: Security Boundaries and Policy](features/Hardening/SECURITY-GAPS.md) - SECURITY-GAPS.md - Draft
- [Security Boundaries — Understanding, System Design and Roadmap](features/Hardening/SECURITY-ROADMAP.md) - SECURITY-ROADMAP.md - Draft

### Impact

- [Impact mapping: impacted test cases for a change](features/Impact/README.md) - README.md - Current
- [Impact Analysis — Algorithm As Built](features/Impact/Algorithm-As-Built.md) - Algorithm-As-Built.md - Draft
- [Impact Analysis — Algorithm Schema](features/Impact/Algorithm-Schema.md) - Algorithm-Schema.md - Draft
- [ImpactDb — Deep Dive](features/Impact/ImpactDb-Deep-Dive.md) - ImpactDb-Deep-Dive.md - Draft
- [Impact Index Storage Relocation — Copilot Prompt Pack](features/Impact/prompts/ImpactIndex-Storage-CopilotPrompts.md) - prompts/ImpactIndex-Storage-CopilotPrompts.md - Draft
- [Regression Selection Algorithm](features/Impact/Regression-Selection-Algorithm.md) - Regression-Selection-Algorithm.md - Draft
- [WAND Dynamic Pruning — Action Plan](features/Impact/WAND-Retrieval-Action-Plan.md) - WAND-Retrieval-Action-Plan.md - Draft

### PipelineBuilder

- [Pipeline Builder](features/PipelineBuilder/README.md) - README.md - Current
- [Install Plans — Feasibility Analysis](features/PipelineBuilder/INSTALL-PLAN-FEASIBILITY.md) - INSTALL-PLAN-FEASIBILITY.md - Draft
- [Requirement: Pipeline Builder (generic guided authoring, WPF + Web)](features/PipelineBuilder/PipelineBuilder_Requirement.md) - PipelineBuilder_Requirement.md - Draft
- [Copilot Prompt — Pipeline Builder (generic authoring, WPF + Web)](features/PipelineBuilder/prompts/PipelineBuilder_Copilot_Prompt.md) - prompts/PipelineBuilder_Copilot_Prompt.md - Draft
- [WatchItem Builder — Implementation Specification](features/PipelineBuilder/WatchItemBuilder_Implementation_Spec.md) - WatchItemBuilder_Implementation_Spec.md - Draft

### PreFlightCheck

- [Pre-flight check](features/PreFlightCheck/README.md) - README.md - Current
- [Test Plan — WatchList, Token Resolution, Template Context and Pre-flight (JVGR22)](features/PreFlightCheck/WATCHLIST-TEST-PLAN-JVGR22.md) - WATCHLIST-TEST-PLAN-JVGR22.md - Draft

### RBAC

- [RBAC: users, roles, permissions, pipeline locks](features/RBAC/README.md) - README.md - Current
- [00 — Master Plan](features/RBAC/00_Master_Plan.md) - 00_Master_Plan.md - Draft
- [01 — System Design](features/RBAC/01_System_Design.md) - 01_System_Design.md - Draft
- [02 — Implementation Roadmap](features/RBAC/02_Implementation_Roadmap.md) - 02_Implementation_Roadmap.md - Draft
- [03 — Integration with the Pipeline Lock Coordination Spec](features/RBAC/03_Integration_With_Lock_Spec.md) - 03_Integration_With_Lock_Spec.md - Draft
- [04 — UI Mockup Catalog](features/RBAC/04_UI_Mockup_Catalog.md) - 04_UI_Mockup_Catalog.md - Draft
- [05 — Default Mode Design](features/RBAC/05_Default_Mode_Design.md) - 05_Default_Mode_Design.md - Draft
- [06 — Copilot Implementation Plan (Token-Efficient)](features/RBAC/06_Copilot_Implementation_Plan.md) - 06_Copilot_Implementation_Plan.md - Draft
- [07 — Implementation Toolkit (start here)](features/RBAC/07_Implementation_Toolkit.md) - 07_Implementation_Toolkit.md - Draft
- [WatchList Tree - Role Combinations](features/RBAC/mockups/RBAC_TreeView_RealHierarchy_Mockup.html) - mockups/RBAC_TreeView_RealHierarchy_Mockup.html - Draft
- [Phase 0.5 Context Pack — Default Mode UX](features/RBAC/phase-packs/phase-0.5-context.md) - phase-packs/phase-0.5-context.md - Draft
- [Phase 0 Context Pack — Identity & Authorization Foundations](features/RBAC/phase-packs/phase-0-context.md) - phase-packs/phase-0-context.md - Draft
- [Phase 1a Context Pack — Login & Identity](features/RBAC/phase-packs/phase-1a-context.md) - phase-packs/phase-1a-context.md - Draft
- [Phase 1b Context Pack — User Management (WPF Admin)](features/RBAC/phase-packs/phase-1b-context.md) - phase-packs/phase-1b-context.md - Draft
- [Phase 2a Context Pack — Pipeline Authorization Guard (Backend Only)](features/RBAC/phase-packs/phase-2a-context.md) - phase-packs/phase-2a-context.md - Draft
- [Phase 2b Context Pack — WPF Capability Gating & Filtered Pipeline View](features/RBAC/phase-packs/phase-2b-context.md) - phase-packs/phase-2b-context.md - Draft
- [Phase 2c Context Pack — Web Client Capability Gating & Filtered Pipeline View](features/RBAC/phase-packs/phase-2c-context.md) - phase-packs/phase-2c-context.md - Draft
- [Phase 3a Context Pack — Pipeline Lock Backend (LockRegistry, Handlers, gRPC Surface)](features/RBAC/phase-packs/phase-3a-context.md) - phase-packs/phase-3a-context.md - Draft
- [Phase 3b Context Pack — WPF Pipeline Lock UI](features/RBAC/phase-packs/phase-3b-context.md) - phase-packs/phase-3b-context.md - Draft
- [Phase 3c Context Pack — Web Client Pipeline Lock UI](features/RBAC/phase-packs/phase-3c-context.md) - phase-packs/phase-3c-context.md - Draft
- [RBAC + Default Mode — Architecture & Change Map](features/RBAC/RBAC_Architecture_And_Change_Map.md) - RBAC_Architecture_And_Change_Map.md - Draft

### Reliability

- [Reliability and UI performance](features/Reliability/README.md) - README.md - Current
- [Tier 1 Reliability & Observability — Command Execution Hardening Spec](features/Reliability/08_Tier1_Reliability_Hardening_Spec.md) - 08_Tier1_Reliability_Hardening_Spec.md - Draft
- [Failure Triage ("Why did this fail?") — Feasibility Audit Prompt](features/Reliability/FailureTriage-Feasibility-AuditPrompt.md) - FailureTriage-Feasibility-AuditPrompt.md - Draft
- [Failure Triage ("Why did this fail?") — Feasibility Audit Report](features/Reliability/FailureTriage-Feasibility-AuditReport.md) - FailureTriage-Feasibility-AuditReport.md - Draft
- [ui-perf-baseline.md](features/Reliability/ui-perf-baseline.md) - ui-perf-baseline.md - Draft
- [UI performance checklist — TestAgentSolution](features/Reliability/ui-perf-checklist.md) - ui-perf-checklist.md - Draft
- [UI Performance Prompt Pack — TestControllerGrpc (WPF) + TestController.WebClient (React)](features/Reliability/UIPerformance-CopilotPrompts.md) - UIPerformance-CopilotPrompts.md - Draft

### RunEmail

- [Consolidated run email](features/RunEmail/README.md) - README.md - Current
- [Enhancement: Redesign TestAgent CI results email (professional, concise, Outlook-native)](features/RunEmail/ISSUE-TestAgentCI-email-redesign.md) - ISSUE-TestAgentCI-email-redesign.md - Draft
- [Consolidated Execution Email - 9 Agents](features/RunEmail/mockups/ConsolidatedExecutionEmail_9Agents_Mockup.html) - mockups/ConsolidatedExecutionEmail_9Agents_Mockup.html - Draft
- [AppServer CI Results](features/RunEmail/mockups/results-email-mockup.html) - mockups/results-email-mockup.html - Draft

### SecurityFramework

- [Security Framework: multi-identity REST authentication](features/SecurityFramework/README.md) - README.md - Current
- [Security Framework Deployment Guide](features/SecurityFramework/DEPLOYMENT-GUIDE.md) - DEPLOYMENT-GUIDE.md - Draft
- [Security Framework Migration Guide](features/SecurityFramework/MIGRATION-GUIDE.md) - MIGRATION-GUIDE.md - Draft
- [Multi-Identity Security Framework](features/SecurityFramework/MultiIdentity_Security_Framework_Requirements.md) - MultiIdentity_Security_Framework_Requirements.md - Draft
- [Security Framework Troubleshooting Runbook](features/SecurityFramework/TROUBLESHOOTING.md) - TROUBLESHOOTING.md - Draft

### XmlEditor

- [WatchList XML editor](features/XmlEditor/README.md) - README.md - Current
- [Prompt — WatchList XML editor intelligence + edit by instruction](features/XmlEditor/prompts/Prompt_XmlEditor_Intelligence.md) - prompts/Prompt_XmlEditor_Intelligence.md - Draft

## UI specs

- [TestController — Test Plan Tree (UI/UX spec)](ui-specs/TestController_TreeView_Spec.html) - Draft
- [TestController_UI_Master_Build_Prompt](ui-specs/TestController_UI_Master_Build_Prompt.md) - Draft
- [TestController — Comprehensive UI Design System & All Views](ui-specs/TestController_UIKit_AllViews_Mockup.html) - Draft
- [TestAgentSolution — UX Improvements Before/After Mockups](ui-specs/UX_Improvements_Mockups.html) - Draft
- [Implementation Prompt: Optimize WatchList Editor Layout](ui-specs/WatchList_Layout_Optimization_Prompt.md) - Draft
- [TestController — Optimized Layout](ui-specs/WatchList_Optimized_Layout_Mockup.html) - Draft
- [WebClient Enhancement Specification](ui-specs/WebClient_Enhancement_Spec.md) - Draft

## Issues (open)

- [ISSUE: Agent Registry & Fleet-View Inconsistency (WPF vs WebClient)](issues/ISSUE_Agent_Fleet_Consistency.md) - Draft
- [Controller health indicators lack threshold coloring, semantic grouping, and clear labels](issues/ISSUE_Controller_Health_Indicators.md) - Draft
- [TestController UI — dropdown theming, gRPC auto-fill, health-flyout overlap, log-pane resize](issues/ISSUE_Dropdown_gRPC_Flyout_LogPane.md) - Draft
- [Consolidate footer into one status bar; escalate critical alerts to a health pill](issues/ISSUE_Health_StatusBar_Consolidation.md) - Draft
- [Sessions dropdown + Controller health — partial implementation; critical UX elements from mockup still missing](issues/ISSUE_Sessions_Health_Implementation_Gaps.md) - Draft
- [Sessions popup overlaps the Controller health strip and critical-alerts line — structural fix required](issues/ISSUE_Sessions_Popup_Overlap_Structural_Fix.md) - Draft
- [Status bar metrics are stacked, tiny, and the health warning is mispositioned](issues/ISSUE_StatusBar_Metrics_Layout.md) - Draft
- [NETOPS-REQ: Agent W25s22Grr1 cannot register with Controller JVGR22 (cross-subnet TCP blocked)](issues/NETOPS-W25s22Grr1-cross-subnet.md) - Draft
- [TestAgentSolution — Six-Feature Build Plan & Copilot Prompt Pack](issues/TestAgent-Feature-Plan-and-CopilotPrompts.md) - Draft
- [Defect Report: WPF Controller — Execution Dashboard & Unified Log Issues](issues/WPF_Controller_Defects_Issues1-7.md) - Draft

## Issues (closed)

- [Implementation Audit — P0 Bugs + P1/P2 Enhancements](issues/closed/CHANGES_P0_P1_P2_Implementation.md) - Archived
- [Copilot Debugging Task: gRPC "Unexpected compressed flag value in message header"](issues/closed/Debug_Grpc_CompressedFlag_Error.md) - Archived
- [Final implementation — live run state across web & WPF](issues/closed/Final_LiveState_Relay_Implementation.md) - Archived
- [Fix Spec: Agent Registration Fails - Missing IAppLogger DI Registration](issues/closed/Fix_AgentRegistration_IAppLogger_DI.md) - Archived
- [Ribbon right-strip layout breaks above 1 active session — needs consolidated Sessions dropdown](issues/closed/ISSUE_Ribbon_Sessions_Dropdown.md) - Archived
- [Pipeline Run — Live State, Lock Enforcement & Attribution](issues/closed/Pipeline_Run_LiveState_Lock_Attribution.md) - Archived
- [Build prompt — single-run pipeline lock + observable broadcast](issues/closed/PipelineLock-SingleRun-Build.md) - Archived
- [Prompt — Investigate and fix: Initialize parameter file, node Tag editing, pre-flight clarity](issues/closed/Prompt_Fix_Initialize_TagEdit_Preflight.md) - Archived
- [Self-Healing gRPC Agent — Implementation Specification](issues/closed/SelfHealing_Grpc_Agent_Spec.md) - Archived
- [WebClient Blank Page in RBAC Secured Mode](issues/closed/WEBCLIENT_BLANK_PAGE_SECURED_MODE.md) - Archived

## Archive

- [execution_dashboard_mockup_v2 (2)](<archive/execution_dashboard_mockup_v2 (2).html>) - Archived
- [Concurrency & Threading Fix Plan](archive/FIX-PLAN-CONCURRENCY.md) - Archived
- [Error Handling & Resilience Fix Plan](archive/FIX-PLAN-ERROR-HANDLING.md) - Archived
- [TestAgentSolution — Production Gap Analysis](archive/GAP-ANALYSIS.md) - Archived
- [Phase 1 — Performance & UX Quick Wins (Implementation Plan)](archive/PHASE1-QUICK-WINS.md) - Archived
- [Phase 2 — UI Ease & Consistency (Implementation Plan)](archive/PHASE2-UI-EASE.md) - Archived
- [Phase 3 — Maintainability (Implementation Plan)](archive/PHASE3-MAINTAINABILITY.md) - Archived
- [Phase 4 — Scale Validation & Test Coverage (Implementation Plan)](archive/PHASE4-SCALE-AND-COVERAGE.md) - Archived
- [Scaling Gap Analysis: 10 → 200 Agents](archive/SCALING-GAP-ANALYSIS-200-AGENTS.md) - Archived
- [Scaling Remediation — Implementation Plan](archive/SCALING-IMPLEMENTATION-PLAN.md) - Archived
- [TestAgentSolution — Implementation Plan (Code-Validated)](archive/TestAgentSolution-Implementation-Plan.md) - Archived
- [TestAgentSolution — Feature Scope & Production-Readiness Roadmap](<archive/TestAgentSolution-Production-Readiness-Scope-and-Roadmap (1).md>) - Archived
- [Tier 3 — Feature Completeness: Gap Implementation Plan](archive/Tier3_Feature_Completeness_Gaps.md) - Archived

## Other

- [FIR test fixtures (sanitized)](FIRFailedTest/Fir/README.md) - Draft

