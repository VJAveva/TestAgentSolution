
# AppServer — Complete Project-Level Structure

**Generated from the open solution.** Projects are grouped by top-level module (under `src\`) and
classified by type: **C++** (`.vcxproj`) and **.NET** (`.csproj`). Use this as the master index for
change tracking, regression mapping, and impact analysis.

**Totals:** ~330 projects across 15 top-level modules.
Legend: `[C++]` native `.vcxproj` · `[NET]` managed `.csproj`

---

## 1. AABootstrap — process supervisor, redundancy arbiter, gRPC bootstrap proxy
- [C++] `Bootstrap\Bootstrap\Bootstrap.vcxproj`
- [C++] `Bootstrap\Bootstrap\Bootstrapps.vcxproj`
- [C++] `Bootstrap\DcomInterfaceAccess\DcomInterfaceAccess.vcxproj`
- [C++] `Bootstrap\PlatformInformationClerk\PlatformInformationClerk.vcxproj`
- [C++] `Bootstrap\PlatformRegistryAccess\PlatformRegistryAccess.vcxproj`
- [C++] `Bootstrap\ProcessRealtimeClerk\ProcessRealtimeClerk.vcxproj`
- [C++] `Bootstrap\ProcessSentry\ProcessSentry.vcxproj`
- [C++] `Bootstrap\SoftwareControlManager\SoftwareControlManager.vcxproj`
- [C++] `FileCopyService\FileCopyService.vcxproj`
- [C++] `PlatformInstallManager\WWPim\WWPim.vcxproj`
- [C++] `PlatformInstallManager\WWPim\WWPimPS.vcxproj`
- [C++] `DCOMTransport\DCOMTransport.vcxproj`
- [C++] `SocketTransport\SocketTransport.vcxproj`
- [NET] `Grpc\Services\AVEVA.AppServer.BootstrapProxy\AVEVA.AppServer.BootstrapProxy.csproj`

## 2. AACategoryPkgs — object category packages
- [C++] `PlatformCategory\PlatformCategory.vcxproj`
- [C++] `UploadServer\UploadServer.vcxproj`
- [C++] `ApplicationCategory\ApplicationCategory.vcxproj`
- [C++] `EngineCategory\EngineCategory.vcxproj`

## 3. AAMxCore — messaging & core runtime
- [C++] `AAMxCoreProxyStub\AAMxCoreProxyStub.vcxproj`
- [C++] `BaseRuntimeComponentServer\BaseRuntimeComponentServer.vcxproj`
- [C++] `Checkpointer\CheckpointFileServer\CheckpointFileServer.vcxproj`
- [C++] `Checkpointer\CheckpointerServer\CheckPointerServer.vcxproj`
- [C++] `Lmx\Lmx.vcxproj`
- [C++] `MessageChannel\MessageChannel.vcxproj`
- [C++] `Nmx\NmxAdptr\NmxAdptr.vcxproj`
- [C++] `NmxSvc\NmxSvc.vcxproj`
- [C++] `NmxSvc\NmxSvcps.vcxproj`
- [C++] `ObjectResources2\objectresources2.vcxproj`
- [C++] `ObjectSyncMgr\ObjectSyncMgr.vcxproj`
- [C++] `sqlite3lib\sqlite3lib.vcxproj`

## 4. AASysObjects — engines, scheduler, redundancy, SDK primitives, editors
### Engine / Scheduler / Redundancy (C++)
- [C++] `AppEngine\AppEnginePrimitivePackage\AppEnginePrimitivePackage.vcxproj`
- [C++] `AppEngine\AppEnginePrimitive\AppEnginePrimitive.vcxproj`
- [C++] `EnginePrimitive\EnginePackage\EnginePackage.vcxproj`
- [C++] `EnginePrimitive\EnginePrimitiveRuntime\EnginePrimitive.vcxproj`
- [C++] `FsEngine\FsEngine.vcxproj`
- [C++] `FsEngine\CommandParser\CommandParser.vcxproj`
- [C++] `Scheduler\Scheduler.vcxproj`
- [C++] `Scheduler\SchedulerPackage\SchedulerPackage.vcxproj`
- [C++] `RedundancyPrimitive\RedundancyPrimitivePRI\RedundancyPrimitivePRI.vcxproj`
- [C++] `RedundancyPrimitive\RedundancyPrimitivePrimitive\RedundancyPrimitivePrimitive.vcxproj`
- [C++] `RedundancyPrimitive\RedundancyPrimitivePackageServer\RedundancyPrimitivePackageServer.vcxproj`
- [C++] `RedundancyPrimitive\RedundancyPrimitiveRuntimeServer\RedundancyPrimitiveRuntimeServer.vcxproj`
- [C++] `CommonPrimitive\CommonPackageServer\CommonPackageServer.vcxproj`
- [C++] `CommonPrimitive\CommonRuntimeServer\CommonRuntimeServer.vcxproj`
- [C++] `SystemManager\SystemManager.vcxproj`
- [C++] `ToolsManager\ToolsManager.vcxproj`
- [C++] `visuallmxtesttool\visuallmxtesttool.vcxproj`
- [C++] `NTPlatform\NTPlatformPackage\NTPlatformPackage.vcxproj`
- [C++] `NTPlatform\NTPlatformPrimitive\NTPlatformPrimitive.vcxproj`
### GlobalDataDistribution (C++)
- [C++] `GlobalDataDistribution\GlobalDataCacheServer\GlobalDataCacheServer.vcxproj`
- [C++] `GlobalDataDistribution\GlobalDataCacheMonitorServer\GlobalDataCacheMonitorServer.vcxproj`
- [C++] `GlobalDataDistribution\GlobalDataDistributionClientRuntime\GlobalDataDistributionClientRuntime.vcxproj`
- [C++] `GlobalDataDistribution\GlobalDataDistributionServiceRuntime\GlobalDataDistributionServiceRuntime.vcxproj`
- [C++] `GlobalDataDistribution\GlobalDataRepositoryProxyServer\GlobalDataRepositoryProxyServer.vcxproj`
- [C++] `GlobalDataDistribution\ProxyStub\ProxyStub.vcxproj`
### Platform Info / Manager (C++)
- [C++] `PlatformInfoServer\PlatformInfoSvr\PlatformInfoSvr.vcxproj`
- [C++] `PlatformManager\MagellanSnapInSvr\MagellanSnapInSvr.vcxproj`
### SDK primitives (C++) — Analog/Input/Output/Extension test objects
- [C++] `Sdk\AnalogRegulatorPrimitive\AnalogRegulatorPackageServer\AnalogRegulatorPackageServer.vcxproj`
- [C++] `Sdk\AnalogRegulatorPrimitive\AnalogRegulatorPrimitive\AnalogRegulatorPrimitive.vcxproj`
- [C++] `Sdk\AnalogRegulatorPrimitive\AnalogRegulatorRuntimeServer\AnalogRegulatorRuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\ExtensionsTestObjectPrimitive\ExtensionsTestObjectC1RuntimeServer\ExtensionsTestObjectC1RuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\ExtensionsTestObjectPrimitive\ExtensionsTestObjectC1C2RuntimeServer\ExtensionsTestObjectC1C2RuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\ExtensionsTestObjectPrimitive\ExtensionsTestObjectC3RuntimeServer\ExtensionsTestObjectC3RuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\ExtensionsTestObjectPrimitive\ExtensionsTestObjectC3C4RuntimeServer\ExtensionsTestObjectC3C4RuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\ExtensionsTestObjectPrimitive\ExtensionsTestObjectPackageServer\ExtensionsTestObjectPackageServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\ExtensionsTestObjectPrimitive\ExtensionsTestObjectRuntimeServer\ExtensionsTestObjectRuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\InputExtensionRuntimeServer\InputExtensionRuntimeServer.vcxproj`
- [C++] `Sdk\InputExtensionPrimitive\InputExtensionPackageServer\InputExtensionPackageServer.vcxproj`
- [C++] `Sdk\InputOutputExtensionPrimitive\InputOutputExtensionOutputPackageServer\InputOutputExtensionOutputPackageServer.vcxproj`
- [C++] `Sdk\InputOutputExtensionPrimitive\InputOutputExtensionOutputRuntimeServer\InputOutputExtensionOutputRuntimeServer.vcxproj`
- [C++] `Sdk\InputOutputExtensionPrimitive\InputOutputExtensionPackageServer\InputOutputExtensionPackageServer.vcxproj`
- [C++] `Sdk\InputOutputExtensionPrimitive\InputOutputExtensionRuntimeServer\InputOutputExtensionRuntimeServer.vcxproj`
- [C++] `Sdk\InputOutputPrimitive\InputOutputPackageServer\InputOutputPackageServer.vcxproj`
- [C++] `Sdk\InputOutputPrimitive\InputOutputPrimitive\InputOutputPrimitive.vcxproj`
- [C++] `Sdk\InputOutputPrimitive\InputOutputRuntimeServer\InputOutputRuntimeServer.vcxproj`
- [C++] `Sdk\InputPrimitive\InputPackageServer\InputPackageServer.vcxproj`
- [C++] `Sdk\InputPrimitive\InputRuntimeServer\InputRuntimeServer.vcxproj`
- [C++] `Sdk\OutputExtensionPrimitive\OutputExtensionPrimitive.vcxproj`
- [C++] `Sdk\OutputExtensionPrimitive\OutputExtensionRuntimeServer\OutputExtensionRuntimeServer.vcxproj`
- [C++] `Sdk\OutputExtensionPrimitive\OutputExtensionPackageServer\OutputExtensionPackageServer.vcxproj`
- [C++] `Sdk\OutputPrimitive\OutputAppObjectServer\OutputAppObjectServer.vcxproj`
- [C++] `Sdk\OutputPrimitive\OutputPrimitive.vcxproj`
- [C++] `Sdk\OutputPrimitive\OutputRuntimeServer\OutputRuntimeServer.vcxproj`
- [C++] `Sdk\QuasiGUIDGenerator\QuasiGUIDGenerator.vcxproj`
- [C++] `Sdk\SDKTestPrimitive\SDKTestPrimitive.vcxproj`
- [C++] `Sdk\SDKTypeLib\SDKTypeLib.vcxproj`
- [C++] `Sdk\VBLoggerWrapper\VBLoggerWrapper.vcxproj`
- [C++] `Sdk\AnalogRegulatorPrimitive\AnalogRegulatorEditorServer\AnalogRegulatorEditorServer.vcxproj`
- [C++] `Sdk\LogDatachangeEventExtensionPrimitive\LogDatachangeEventExtRuntimeSvr\LogDatchangeEventExtRuntimeServer.vcxproj`
- [C++] `Sdk\LogDatachangeEventExtensionPrimitive\LogDatachangeEventExtPackageSvr\LogDatachangeEventExtPackageServer.vcxproj`
### .NET editors / engines
- [NET] `WebViewEngine\WebViewEngineEditor\WebViewEngineEditor.csproj`
- [NET] `DotNetObjectEditors\Archestra.Editors.CustomControls\Archestra.Editors.CustomControls.csproj`
- [NET] `DotNetObjectEditors\WinPlatformEditor\WinPlatformEditor.csproj`
- [NET] `DotNetObjectEditors\ApplicationEngineEditor\ApplicationEngineEditor.csproj`
- [NET] `CloudPlatform\CloudPlatformEditor\CloudPlatformEditor.csproj`

## 5. AppObjCommon — utility primitives (Analog/Boolean/Level/ROC/Scaling)
- [C++] `UtilityPrimitives\AnalogExtension\AnalogExtensionRuntimeServer\AnalogExtensionRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\AnalogExtension\AnalogExtensionPackageServer\AnalogExtensionPackageServer.vcxproj`
- [C++] `UtilityPrimitives\AnalogStatisticsPrimitive\AnalogStatisticsPackageServer\AnalogStatisticsPackageServer.vcxproj`
- [C++] `UtilityPrimitives\AnalogStatisticsPrimitive\AnalogStatisticsPRI\AnalogStatisticsPRI.vcxproj`
- [C++] `UtilityPrimitives\AnalogStatisticsPrimitive\AnalogStatisticsPrimitive\AnalogStatisticsPrimitive.vcxproj`
- [C++] `UtilityPrimitives\AnalogStatisticsPrimitive\AnalogStatisticsRuntimeServer\AnalogStatisticsRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\BooleanAlarmsPrimitive\BooleanAlarmsPRI\BooleanAlarmsPRI.vcxproj`
- [C++] `UtilityPrimitives\BooleanAlarmsPrimitive\BooleanAlarmsPrimitive\BooleanAlarmsPrimitive.vcxproj`
- [C++] `UtilityPrimitives\BooleanAlarmsPrimitive\BooleanAlarmsRuntimeServer\BooleanAlarmsRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\BooleanAlarmsPrimitive\BooleanAlarmsPackageServer\BooleanAlarmsPackageServer.vcxproj`
- [C++] `UtilityPrimitives\BooleanExtension\BooleanUtilitiesPackageServer\BooleanUtilitiesPackageServer.vcxproj`
- [C++] `UtilityPrimitives\BooleanExtension\BooleanUtilitiesRuntimeServer\BooleanUtilitiesRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\BooleanStatisticsPrimitive\BooleanStatisticsPRI\BooleanStatisticsPRI.vcxproj`
- [C++] `UtilityPrimitives\BooleanStatisticsPrimitive\BooleanStatisticsPrimitive\BooleanStatisticsPrimitive.vcxproj`
- [C++] `UtilityPrimitives\BooleanStatisticsPrimitive\BooleanStatisticsRuntimeServer\BooleanStatisticsRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\DeviationAlarmsPrimitive\DeviationAlarmsPrimitivePrimitive\DeviationAlarmsPrimitivePrimitive.vcxproj`
- [C++] `UtilityPrimitives\DeviationAlarmsPrimitive\DeviationAlarmsPrimitiveRuntimeServer\DeviationAlarmsPrimitiveRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\DeviationAlarmsPrimitive\DeviationAlarmsPrimitivePackageServer\DeviationAlarmsPrimitivePackageServer.vcxproj`
- [C++] `UtilityPrimitives\DeviationAlarmsPrimitive\DeviationAlarmsPrimitivePRI\DeviationAlarmsPrimitivePRI.vcxproj`
- [C++] `UtilityPrimitives\LevelAlarmsPrimitive\LevelAlarmsPRI\LevelAlarmsPRI.vcxproj`
- [C++] `UtilityPrimitives\LevelAlarmsPrimitive\LevelAlarmsPrimitive\LevelAlarmsPrimitive.vcxproj`
- [C++] `UtilityPrimitives\LevelAlarmsPrimitive\LevelAlarmsRuntimeServer\LevelAlarmsRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\LevelAlarmsPrimitive\LevelAlarmsPackageServer\LevelAlarmsPackageServer.vcxproj`
- [C++] `UtilityPrimitives\ROCAlarmsPrimitive\ROCAlarmsPRI\ROCAlarmsPRI.vcxproj`
- [C++] `UtilityPrimitives\ROCAlarmsPrimitive\ROCAlarmsPrimitive\ROCAlarmsPrimitive.vcxproj`
- [C++] `UtilityPrimitives\ROCAlarmsPrimitive\ROCAlarmsRuntimeServer\ROCAlarmsRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\ROCAlarmsPrimitive\ROCAlarmsPackageServer\ROCAlarmsPackageServer.vcxproj`
- [C++] `UtilityPrimitives\ScalingExtension\ScalingExtensionRuntimeServer\ScalingExtensionRuntimeServer.vcxproj`
- [C++] `UtilityPrimitives\ScalingExtension\ScalingExtensionPackageServer\ScalingExtensionPackageServer.vcxproj`
- [C++] `UtilityPrimitives\ScalingPrimitive\ScalingPackageServer\ScalingPackageServer.vcxproj`
- [C++] `UtilityPrimitives\ScalingPrimitive\ScalingPRI\ScalingPRI.vcxproj`
- [C++] `UtilityPrimitives\ScalingPrimitive\ScalingPrimitive\ScalingPrimitive.vcxproj`
- [C++] `UtilityPrimitives\ScalingPrimitive\ScalingRuntimeServer\ScalingRuntimeServer.vcxproj`

## 6. ArchestraObj — device app objects (Analog/Discrete/Field/SQL/Switch)
- [C++] `AppObjects\AnalogDevicePrimitive\AnalogDevicePackageServer\AnalogDevicePackageServer.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\AnalogDevicePrimitive\AnalogDevicePrimitive.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\AnalogDeviceRuntimeServer\AnalogDeviceRuntimeServer.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\DevAlarmsPrimitive\DevAlarmsPackageServer\DevAlarmsPackageServer.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\DevAlarmsPrimitive\DevAlarmsPrimitive\DevAlarmsPrimitive.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\DevAlarmsPrimitive\DevAlarmsRuntimeServer\DevAlarmsRuntimeServer.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\SPPrimitive\SPPackageServer\SPPackageServer.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\SPPrimitive\SPPrimitive\SPPrimitive.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\SPPrimitive\SPRuntimeServer\SPRuntimeServer.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\LevelAlarmsPrimitive\LevelAlarmsPackageServer\LevelAlarmsPackageSvr.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\LevelAlarmsPrimitive\LevelAlarmsPrimitive\LevelAlarmPrimitive.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\LevelAlarmsPrimitive\LevelAlarmsRuntimeServer\LevelAlarmsRuntimeSvr.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\ScalingPrimitive\ScalingPackageServer\ScalingPackageSvr.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\ScalingPrimitive\ScalingPrimitive\ScalePrimitive.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\ScalingPrimitive\ScalingRuntimeServer\ScalingRuntimeSvr.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\ROCAlarmsPrimitive\ROCAlarmsRuntimeServer\ROCAlarmsRuntimeSvr.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\ROCAlarmsPrimitive\ROCAlarmsPrimitive\ROCAlarmPrimitive.vcxproj`
- [C++] `AppObjects\AnalogDevicePrimitive\ROCAlarmsPrimitive\ROCAlarmsPackageServer\ROCAlarmsPackageSvr.vcxproj`
- [C++] `AppObjects\DiscreteDevicePrimitive\*` (Package/Runtime/Stats/FB/Cmd/Editor — 10 projects)
- [C++] `AppObjects\FieldReferencePrimitive\FieldReference{Editor,Package,Primitive,Runtime}Server.vcxproj` (4)
- [C++] `AppObjects\SQLDataPrimitive\MyOdbcLib\MyOdbcLib.vcxproj`, `SSPLogon\SSPLogon.vcxproj`
- [C++] `AppObjects\SwitchPrimitive\Switch{Package,PDF,Primitive,Runtime}*.vcxproj` (4)
- [NET] `AppObjects\AnalogDevicePrimitive\AnalogDeviceEditor\AnalogDeviceEditor.csproj`
- [NET] `AppObjects\DiscreteDevicePrimitive\DiscreteDeviceEditor\DiscreteDeviceEditor.csproj`
- [NET] `AppObjects\FieldReferencePrimitive\FieldReferenceEditor\FieldReferenceEditor.csproj`
- [NET] `AppObjects\SwitchPrimitive\SwitchEditor\SwitchEditor.csproj`

## 7. ArchSvc — services (MXData, OPC-UA, GalaxyBrowser, Auth, IMethod, Config, Install)
- [C++] `MXDataServer\MxDataConsumer\MxDataConsumerInterop\*` (+Version)
- [C++] `MXDataServer\MxDataProvider\MxDataProviderInterop\*` (+Version), `MxDataProvider\MxDataProvider.vcxproj`
- [NET] `MXDataServer\MxDataConsumer\MxConsumerAbstractionUnitTests\Archestra.MxDataConsumer.Abstractions.UnitTests.csproj` **(TEST)**
- [NET] `MXDataServer\MxDataConsumer\Archestra.MxDataConsumer.Abstractions\...csproj`
- [NET] `MXDataServer\MxDataConsumer\MxDataConsumer\MxDataConsumer.csproj`
- [NET] `MXDataServer\MxDataService\MxDataServiceHost\MxDataServiceHost.csproj`
- [NET] `MXDataServer\MxDataService\ArchestrAServices.MxDataService\...csproj`
- [NET] `MXDataServer\ArchestrA.Editors.MXDataService\...csproj`
- [NET] `OPCUAService\RealtimeDataAccessProvider\RealtimeDataAccess.csproj`
- [NET] `OPCUAService\OPCUAHostEditor\OPCUAHostEditor.csproj`
- [NET] `OPCUAService\RealTimeBrowseAccess\RealTimeBrowseAccess.csproj`
- [NET] `OPCUAService\OPCUAHost\OPCUAHost.csproj`
- [NET] `GalaxyBrowserService\*` (DataProvider, Host, Common, Services — 5 projects)
- [NET] `RuntimeDataService\Host\AVEVA.RuntimeDataProvider.Host.csproj`
- [NET] `aaUserAuthenticationAD\UserAuthenticationAD\aaUserAuthenticationAD.csproj`
- [NET] `aaUserAuthenticationAD\ArchestrA.Editors.AuthenticationService\...csproj`
- [NET] `IMethod\IMethodLibrary\aaMethods\aaMethods.csproj`, `TestMethodClient\TestMethodClient.csproj`, `BuildAASLib\BuildAASLib.csproj`
- [NET] `Configuration\ArchestrA.PlatformNodes\...csproj`, `ArchestrA.GRBrowserEditor\...GalaxyBrowserService.csproj`
- [NET] `Install\AppServerInTouchConfigurator\...csproj`, `AppServerConfigurator\...csproj`

## 8. IDEExtensions — aaIDE.exe shell, views, snapins, ASB
- [NET] `IDEShell\GalaxyExplorer\GalaxyExplorer.csproj`  ← **produces aaIDE.exe**
- [NET] `IDEShell\BackstageHomeCtrl\BackstageHome.csproj`
- [NET] `IDEShell\GalaxyBrowser\GalaxyBrowser.csproj`
- [NET] `MainIDEForm\MainIDEForm.csproj`
- [NET] `Views\ApplicationViews\ApplicationViews.csproj`  ← **EngineInfoService (build-number consumer)**
- [NET] `Views\TemplateView\TemplateView.csproj`
- [NET] `Views\LibraryViews\LibraryViews.csproj`
- [NET] `Views\IDETreeView\IDETreeView.csproj`
- [NET] `Views\FlatViews\FlatViews.csproj`
- [NET] `Views\PropertyControls\PropertyControls.csproj`
- [NET] `Views\ArchestraBrowserControls\ArchestraBrowserControls.csproj`
- [NET] `Views\IOMappingView\*` (View, MxAccessProxy, AutoBindIOCtrl, AutoBindIOApp — 4 projects)
- [NET] `ASB\ArchestrA.IDE.ArchestrAServices.View\...csproj`
- [NET] `ASB\MultiGalaxySvcExtension\GRNodePairingExtension\...csproj`, `DiscoveryServiceExtension\...csproj`
- [NET] `Snapins\ObjectOperations\ObjectOperations.csproj`, `Snapins\GalaxyOperations\GalaxyOperations.csproj`

## 9. ObjectFramework — editor framework, attributes tab, skinning, wizards
- [C++] `EditorFramework\CASInterop\*` (+Version), `COMCAS\COMCAS.vcxproj`
- [NET] `EditorFramework\GalaxyGraphicHost\GalaxyGraphicHost.csproj`, `EditorManager\EditorManager.csproj`
- [NET] `EditorFramework\ArchestraEditorFramework\...csproj`, `ConfigurationAccessComponent\...csproj`, `IaaEditorFormLib\...csproj`
- [NET] `AttributesTabHost\*` (InstanceWizard, AttributesWinFormHost, InstanceWizardHost, SymbolConfigurator, AttributesTab — 5 projects)
- [NET] `SkinningContainer\SkinningContainer\...csproj`, `SkinningPreviewControl\...csproj`
- [NET] `ObjectSymbolWizard\...csproj`, `ObjectWizardConfigurator\...csproj`, `CommonSymbolDisplayPackage\...csproj`

## 10. PFServer / PFClient — Galaxy Repository, script, package mgmt, CRLink
- [C++] `PFServer\WWPackageServer\...`, `ClusterFactoryObject\*`, `ConnectionPoolSupport\*`
- [C++] `PFServer\CRLink\*` (Diagnostic, ProtDCOM, ProtGrpc, Server, Service, Client — 6 projects)
- [C++] `PFServer\GalaxyRepositoryAdmin\*` (SnapIn, Client, Controller, Admin — 4 projects)
- [C++] `PFServer\ImportLibrary\ImportLibrary\*`, `TypeLibs\*`; `ObjectCacheSvr\*`; `ScriptPrimitive\Script{Package,Runtime}\*`
- [C++] `PFServer\VisualElementPrimitive\*`, `WWCdi\*`, `WWFsObject\*`, `xxGalaxy\*` (+Package)
- [C++] `PFClient\WWPackageManager\*`, `WWPDFManager\*`, `GRAccess\GRAccessApp\*`
- [NET] `PFServer\CRLink\gRPC\Service\AVEVA.Appserver.GRProxy\...csproj`
- [NET] `PFServer\ScriptPrimitive\ScriptRuntime.Net\...csproj`, `ScriptPackage.Net\...csproj`
- [NET] `PFServer\SmoNugetPackage\...csproj`, `GRLoadServer\GRLoadServer.csproj`, `AACache\AACache.csproj`
- [NET] `PFServer\AssociatedFileAccess\AssociatedFileAccessor\...csproj`
- [NET] `PFServer\Install\AAPostInstall\*` (aaAuthenticationPlugin, AAPostInstall, Appserver.Grpc.Plugin, aaGRLicensingPlugin), `GRPostInstallCleanup\*`
- [NET] `PFServer\ImportLibrary\SimpleCsc\...csproj`, `SimpleTlbImp\...csproj`
- [NET] `PFServer\Utils\SetObjStatus\...csproj`
- [NET] `PFClient\GRAccess\GRAccess\GRAccess.csproj`, `PackageManagerNet\PackageManagerNet.csproj`

## 11. MagellanHistory — alarm & process history
- [C++] `AlarmHistory\*` (AlarmExtension R/P, Alarm R/P, ITAlarmProvider, NotificationConsumerHelper, xxEvent, NativeEventWrapper, BadValueAlarmExt R/P, NotificationDistributor — ~13 projects)
- [C++] `ProcessHistory\*` (Historian R/P, HistoryExtension R/P, History R/P, AreaHierarchy(+Svr), xxMDASWrapper, TagnameRegistrar PRI/Primitive/Runtime — ~13 projects)
- [C++] `BulkHistoryPrimitive\*` (Runtime, Primitive, PRI — 3), `AreaObject\Area{Runtime,Package}Server\*`
- [NET] `DotNetObjectEditors\AreaEditor\AreaEditor.csproj`, `BulkHistoryClient\BHClientEditor\BHClientEditor.csproj`

## 12. Device Integration & Proxies
- [C++] `DDESuiteLinkClientObj\SuiteLinkDDEProxyPrimitive\*` (3)
- [C++] `DiCommon\BlockRead\*` (Runtime, Editor, Primitive, Package — 4), `BlockWrite\*` (4)
- [C++] `DiCommon\IOHierarchy\*` (Runtime, Editor, Package — 3), `ScanGroup\*` (Runtime, Package — 2)
- [C++] `InTouchProxyObj\*` (Package, Runtime x2, Servers, Primitive x2 — ~7)
- [C++] `OPCClientObj\OpcEnumLibInterop\*` (+Version), `OPCProxyPrimitive\*` (3)
- [C++] `RedundantDIObject\RedundantDIObjectPrimitive\*` (Primitive, Servers, Runtime, Package — 4)
- [C++] `SQLDataObj\...\SQLDataPackageServer\SequelDataPackageServer.vcxproj`
- [NET] `DiCommon\ScanGroupControl\ScanGroupControl.csproj`
- [NET] `InTouchProxyObj\...\InTouchProxyEditor\InTouchProxyEditor.csproj`
- [NET] `OPCClientObj\OPCClientEditor\OPCClientEditor.csproj`
- [NET] `RedundantDIObject\...\RedundantDIObjectEditor\RedundantDIObjectEditor.csproj`
- [NET] `SQLDataObj\...\SQLDataRuntimeServer\SequelDataRuntimeServer.csproj`, `SQLDataEditor\SequelDataEditor.csproj`
- [NET] `DDESuiteLinkClientObj\...\DDESuiteLinkClientEditor\DDESuiteLinkClientEditor.csproj`

## 13. Security
- [C++] `aaUserValidator\UserValidatorPS.vcxproj`, `aaUserValidator\aaUserValidator.vcxproj`
- [C++] `Security\LoginDialogServer\LoginDialogServer.vcxproj`, `SmartCardAL\SmartCardAL.vcxproj`, `xxSecurity\xxSecurity.vcxproj`
- [NET] `AuthenticateServiceDiscovery\AuthServiceDiscovery\AuthServiceDiscovery.csproj`
- [NET] `Security\LoginDialogServer\AVEVA.LoginDialog\AVEVA.LoginDialog.csproj`
- [NET] `Security\xxSecurityEditor\xxSecurityEditor\xxSecurityEditor.csproj`
- [NET] `Security\xxSecurityEditor\RoleEnumerationTest\RoleEnumeration\RoleEnumerationTest.csproj` **(TEST)**

## 14. Common / Abstraction / gRPC infrastructure
- [NET] `Common\Appserver.gRPC.Common\AVEVA.AppServer.ServiceCommon\AVEVA.AppServer.GRPC.ServicesCommon.csproj`
- [NET] `Common\Appserver.gRPC.Common\AVEVA.AppServer.BootstrapProxy.Client\...csproj`  ← **managed relay client**
- [NET] `Common\Appserver.gRPC.Common\AVEVA.IdentityManager.Proxy\...csproj`
- [NET] `Common\ASBConfigServiceWrapper\ASBConfigSvcWrapper\...csproj`, `ArchestrA.Configuration.aaSRImportExport\...csproj`
- [NET] `Abstraction\Appserver.Client.Abstraction\...csproj`

## 15. Other modules
- **Sequencer:** [C++] `SequencerPackageServer`, `SequencerPrimitivePDF`, `SequencerRuntimeServer`; [NET] `SequencerEditor.csproj`
- **UserExtended:** [C++] Analog/User/Boolean utilities (Runtime+Package); [NET] `UserDefinedEditor.csproj`
- **IOMBLS:** [NET] `GalaxyBrowseV4Service.csproj`, `ConfigVRService.csproj`, `IOMBLSServiceHost.csproj`, `IOMBLSImplementation.csproj`
- **Morpheus:** [C++] `Install\CustomAction\...`; [NET] `GalaxySupportUtility.csproj`, `aaTagMappingUtility.csproj`, `aaTagImportWizard.csproj`
- **ArchScript:** [NET] `RestoreScripts.csproj`, `MxSupport.csproj`, `ClientLibrary.csproj`, `FunctionCategory.csproj`, `MxAttribute.csproj`
- **SQLScript:** [NET] `aaDBIntegration.csproj`

---

## Test projects (regression anchors)
| Test project | Module | Type |
|---|---|---|
| `AVEVA.AppServer.BootstrapProxyTests.csproj` | AABootstrap gRPC proxy | [NET] MSTest |
| `Archestra.MxDataConsumer.Abstractions.UnitTests.csproj` | ArchSvc MXData | [NET] Unit |
| `RoleEnumerationTest.csproj` | Security | [NET] |
| `TestMethodClient.csproj` | ArchSvc IMethod | [NET] |

---

### Notes
- **aaIDE.exe** is built by `IDEExtensions\IDEShell\GalaxyExplorer\GalaxyExplorer.csproj`.
- Native (`.vcxproj`) projects dominate the runtime/primitive layers; managed (`.csproj`) projects
  dominate IDE, editors, and modern gRPC services.
- Counts per module are indicative; a few large families (DiscreteDevice, AlarmHistory,
  ProcessHistory) are summarized with counts rather than every leaf enumerated to keep this readable —
  ask me to expand any module fully if needed.
