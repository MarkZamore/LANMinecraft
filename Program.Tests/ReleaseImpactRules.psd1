# Exact paths only. Anything outside the listed components requires the full Release profile.
# Suite dependencies add coverage for linked views and callers of the changed component.
@{
    Always = @(
        'ChangelogTests.TheNewestVersion_IsTheCommitItIsWrittenFor'
        'ChangelogTests.EveryVersion_IsTwoShortSentences'
        'ReleaseWorkflowTests.BuildAndVerificationRunIndependently_AndBothGatePublication'
        'ReleaseWorkflowTests.BuildCreatesDeltasBeforeStaging_AndPublishUsesThatArtifact'
        'ReleaseWorkflowTests.PublicationRequiresScanEvidenceForTheStagedExecutable'
        'ReleaseWorkflowTests.TheDependencyAudit_IsPartOfEveryRestore'
        'FunctionalAuditTests.Updater_FullDeltaFallbackCancellationInstallRestartAndRollback'
        'NativePortableHostTests.OuterResourcesAndPacketKeepTheProductIdentityWithoutTheUnusedSdkApphost'
        'NativePortableHostTests.ColdAndWarmStartsUseThePrivateRuntimePortableTempAndTheOuterStaProcess'
        'NativePortableHostTests.NativeUpdaterWaitsForTheOuterProcessAndRestartsTheVerifiedReplacementAtItsPortableRoot'
    )
    Suites = @{
        Changelog = @{
            DependsOn = @()
            Methods = @('WindowCanvasTests.SteamStatus_RemainsReadableAndAlignedWithTheChangelog')
        }
        LauncherLayout = @{
            DependsOn = @()
            Methods = @(
                'DesignTokensTests.AppXaml_DefinesEveryTokenDesignMdNames_AndNoOthers'
                'UiPreparationLayoutTests.PreparingHiddenAttachedContentPreservesItsParentsLayoutSlot'
                'WindowCanvasTests.SteamStatus_RemainsReadableAndAlignedWithTheChangelog'
                'WindowCanvasTests.MaintenanceStatusFitsNextToTheMotto'
                'WindowCanvasTests.ReturningFromLibrary_PreservesAutomaticScrollbarVisibility'
                'WindowCanvasTests.TheLayout_KeepsTwoColumnsAndEveryExpandedControlReachable'
                'WindowCanvasTests.TheExternalScrollbar_SpansPlayAndBodyAndScrollsOnlyTheBody'
                'WindowCanvasTests.TheNativeClient_MaximizesAndRestoresWithoutChangingColumnsOrSelection'
                'WindowCanvasTests.OpenDropDown_LocksOnlyTheParentScrollAndReleasesItOnClose'
            )
        }
        NicknamePresentation = @{
            DependsOn = @('LauncherLayout')
            Methods = @(
                'NameMarqueeTests.HiddenAndUnloadedFieldsReleaseTheirClockAndResumeWhenShown'
                'NameMarqueeTests.MinimizedWindowsReleaseTheClockAndResumeOnlyForVisibleOverflow'
            )
        }
        LauncherDropDown = @{
            DependsOn = @('LauncherLayout')
            Methods = @('DropDownPreloadPlacementTests.FirstOpen_UsesTheControlsScreenBoundsBeforeAndAfterHiddenPreparation')
        }
        LibraryPresentation = @{
            DependsOn = @('LauncherLayout')
            Methods = @(
                'LibraryAvailabilityTests.PreparationAndRunningGameLeaveCatalogueAndDetailsScrollable'
                'LibraryAvailabilityTests.LibraryScrollbarIsAutomaticWhenItsContentDoesNotOverflow'
                'LibraryOverviewPreparationTests.CollapsedOverviewWarmsAsOneBatchWithoutConstructingUnopenedDetailPages'
                'LibraryOverviewPreparationTests.RemovingAnOpenPackPrunesItsPageEvenWhileTheNextOverviewIsLoading'
                'LibraryOverviewPreparationTests.FirstFixedCardKeepsItsLeftAnchorWhenMoreCardsAppear'
                'LibraryPreviewScrollTests.SixPreparedPreviewsAllowTheLastCatalogRowToScrollIntoView'
                'LibraryStartupRefreshTests.RepeatedAtomicSnapshotsKeepTheOpenDetailPageAndItsState'
                'PackCatalogTests.LibraryFitsRealControlsAndTreeExpansionSurvivesNavigation'
            )
        }
        LibraryGroups = @{
            DependsOn = @('LibraryPresentation')
            Methods = @(
                'CatalogModGroupTests.GroupedRootsKeepMetadataOrderChildrenAndTheCompleteInventory'
                'CatalogModGroupTests.AbsentGroupsKeepFlatCardsAndAnEmptySelectionKeepsNoSections'
                'CatalogModGroupTests.PublishedGroupsRejectDuplicatesHiddenLibrariesChildrenAndIncompleteCoverage'
                'CatalogModGroupTests.UnknownCountScopesAreRejectedAndAllContentRemainsIndependentOfGroups'
                'CatalogModGroupTests.NormalModTreesKeepLinksSeparateAndSizeChevronsToTextInGroupedAndFlatCards'
                'CatalogPresentationTests.LibraryProjectsGroupVisibleModulesWithoutAddingToTheInventory'
                'CatalogPresentationTests.EmptyNativeDetailHidesTheModSectionAndRoutesOnlyTheProjectLink'
                'PackCatalogTests.FilteredDisplayCountsNeverReplaceThePublishedNativeMemoryCount'
            )
        }
        LibraryLinks = @{
            DependsOn = @('LibraryGroups')
            Methods = @(
                'PackCatalogTests.AllSeedModsHaveProjectPagesAndOldCachedMetadataUsesTheSameOfflineLinks'
                'PackCatalogTests.ModAddonAndLibraryLinksOpenTheirOwnPagesWithoutChangingExpansion'
                'PackCatalogTests.UnsafeProjectPageCannotReplaceTheVerifiedCatalogue'
                'PackCatalogTests.NativeCardUsesTheVerifiedProjectVersionAndRejectsAnUnsafeUpdate'
            )
        }
        LibraryMotion = @{
            DependsOn = @('LibraryGroups')
            Methods = @('LibraryTreeMotionTests.AddonReveal_ReversesWithoutJumpAndRestoresAutomaticLayoutAfterNavigation')
        }
        BackdropRendering = @{
            DependsOn = @('LibraryPresentation')
            Methods = @(
                'BackdropSurfaceBorderTests.PreparedFillMatchesTheNativePhotoAndRoundedSurfaceAtEveryRenderDpi'
                'BackdropSurfaceBorderTests.MovingTheSurfaceKeepsItsPhotographAnchoredToTheWindow'
                'BackdropSurfaceBorderTests.ResourceReplacementAndMutableFillKeepTheLiveExpressionAndLatestColors'
                'BackdropSurfaceBorderTests.NestedPaintedParentsAndUnmarkedArtworkKeepTheirOriginalFills'
                'BackdropSurfaceBorderTests.TransparentBackdropNeverUsesAnOpaquePreparedSurface'
                'PreparedWindowBackdropTests.FrozenBackdropMatchesTheOriginalAtTheCurrentPhysicalClientSize'
                'PreparedWindowBackdropTests.ResizeAndSourceChangesUseOneWorkerAndOnlyPublishTheLatestGeneration'
                'PreparedWindowBackdropTests.ClosingWithAPendingWorkerCannotPublishOrLeaveQueueWorkBehind'
                'PreparedWindowBackdropTests.FailedPreparationKeepsTheOriginalWithoutSpinningAndCanRefreshAgain'
                'PreparedWindowBackdropTests.AResourceChangeStillUpdatesTheBackdropAfterThePreparedBrushWasApplied'
                'PreparedStaticRenderingTests.CroppedPreparationMatchesNativeImageAtTheCurrentViewportAndKeepsTheSource'
                'PreparedStaticRenderingTests.ImageWorkersSerializeRasterWorkSkipObsoleteVariantsAndPrioritizeVisibleRequests'
            )
        }
        MaintenanceStatus = @{
            DependsOn = @('LauncherLayout')
            Methods = @(
                'LauncherMaintenanceTests.GameTransitionsDoNotCancelAnInFlightOperationAndShutdownDoesNotWait'
                'LauncherMaintenanceTests.ProgressCountsDistinctCompletedStepsAndFinishesAfterRemovingCheckpoint'
                'LauncherMaintenanceTests.ProgressResumesFromSavedStepsAndWaitsForTheDeferredPack'
            )
        }
    }
    Rules = @(
        @{ Paths = @('Program/Changelog.md'); Suites = @('Changelog') }
        @{ Paths = @('Program/AdaptiveLauncherLayout.cs'); Suites = @('LauncherLayout') }
        @{ Paths = @('Program/NameMarquee.cs'); Suites = @('NicknamePresentation') }
        @{ Paths = @('Program/CenteredDropDown.xaml', 'Program/CenteredDropDown.xaml.cs'); Suites = @('LauncherDropDown') }
        @{ Paths = @('Program/CatalogLibraryTree.cs', 'Program/CatalogModGroup.cs'); Suites = @('LibraryGroups') }
        @{ Paths = @('Program/CatalogModPages.cs'); Suites = @('LibraryLinks') }
        @{ Paths = @('Program/LibraryTreeMotion.cs'); Suites = @('LibraryMotion') }
        @{
            Paths = @('Program/BackdropSurfaceBorder.cs', 'Program/BackdropSurfaceCache.cs', 'Program/PreparedWindowBackdrop.cs')
            Suites = @('BackdropRendering')
        }
        @{ Paths = @('Program/MainWindow.MaintenanceStatus.cs'); Suites = @('MaintenanceStatus') }
    )
}
