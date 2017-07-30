﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using AsyncRpc;
using AsyncRpc.Transport.Tcp;
using AvalonStudio.MSBuildHost;
using AvalonStudio.Platforms;
using AvalonStudio.Projects.OmniSharp.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using RoslynPad.Editor.Windows;
using RoslynPad.Roslyn.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace RoslynPad.Roslyn
{
    public sealed class RoslynWorkspace : Workspace
    {
        private CompositionHost _compositionContext;
        private readonly NuGetConfiguration _nuGetConfiguration;
        private readonly Dictionary<DocumentId, AvalonEditTextContainer> _openDocumentTextLoaders;

        private readonly ConcurrentDictionary<DocumentId, Action<DiagnosticsUpdatedArgs>> _diagnosticsUpdatedNotifiers;

        private IMsBuildHostService msBuildHostService;
        public DocumentId OpenDocumentId { get; private set; }


        internal RoslynWorkspace(HostServices host, NuGetConfiguration nuGetConfiguration, CompositionHost compositionContext)
            : base(host, WorkspaceKind.Host)
        {
            _nuGetConfiguration = nuGetConfiguration;

            msBuildHostService = new Engine().CreateProxy<IMsBuildHostService>(new TcpClientTransport(IPAddress.Loopback, 9000));

            _openDocumentTextLoaders = new Dictionary<DocumentId, AvalonEditTextContainer>();
            _diagnosticsUpdatedNotifiers = new ConcurrentDictionary<DocumentId, Action<DiagnosticsUpdatedArgs>>();

            _compositionContext = compositionContext;
            GetService<IDiagnosticService>().DiagnosticsUpdated += OnDiagnosticsUpdated;

            this.EnableDiagnostics(DiagnosticOptions.Semantic | DiagnosticOptions.Syntax);
        }

        private void OnDiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs diagnosticsUpdatedArgs)
        {
            var documentId = diagnosticsUpdatedArgs?.DocumentId;

            if (documentId == null) return;

            if (_diagnosticsUpdatedNotifiers.TryGetValue(documentId, out var notifier))
            {
                notifier(diagnosticsUpdatedArgs);
            }
        }

        public TService GetService<TService>()
        {
            return _compositionContext.GetExport<TService>();
        }

        public async Task<Tuple<Project, List<string>>> AddProject(string solutionDir, string projectFile)
        {
            var res = await msBuildHostService.GetVersion();

            var properties = new List<Property>();

            properties.Add(new Property { Key = "DesignTimeBuild", Value = "true" });
            properties.Add(new Property { Key = "BuildProjectReferences", Value = "false" });
            properties.Add(new Property { Key = "_ResolveReferenceDependencies", Value = "true" });
            properties.Add(new Property { Key = "SolutionDir", Value = solutionDir });
            properties.Add(new Property { Key = "ProvideCommandLineInvocation", Value = "true" });
            properties.Add(new Property { Key = "SkipCompilerExecution", Value = "true" });

            var assemblyReferences = await msBuildHostService.GetTaskItem("ResolveAssemblyReferences", projectFile, properties);
            
            var projectReferences = await msBuildHostService.GetProjectReferences(projectFile);

            var id = ProjectId.CreateNewId();
            OnProjectAdded(ProjectInfo.Create(id, VersionStamp.Create(), Path.GetFileNameWithoutExtension(projectFile), "", LanguageNames.CSharp, projectFile));

            foreach (var reference in assemblyReferences.Data.Items)
            {
                OnMetadataReferenceAdded(id, MetadataReference.CreateFromFile(reference.ItemSpec));

                foreach(var item in reference.Metadatas)
                {
                    Console.WriteLine(item.Name, item.Value);
                }
            }

            return new Tuple<Project, List<string>>(CurrentSolution.GetProject(id), projectReferences.Data);
        }

        public ProjectId GetProjectId(AvalonStudio.Projects.IProject project)
        {
            var projects  = CurrentSolution.Projects.Where(p => p.FilePath.CompareFilePath(Path.Combine(project.Location)) == 0);

            if(projects.Count() != 1)
            {
                throw new NotImplementedException();
            }

            return projects.First().Id;
        }

        public void ResolveReference(AvalonStudio.Projects.IProject project, string reference)
        {
            var projects = CurrentSolution.Projects.Where(p => p.FilePath.CompareFilePath(Path.Combine(project.LocationDirectory, reference)) == 0);

            if(projects.Count() != 1)
            {
                throw new NotImplementedException();
            }

            var referencedProject = projects.First();

            OnProjectReferenceAdded(GetProjectId(project), new ProjectReference(referencedProject.Id));
        }

        public DocumentId AddDocument(Project project, AvalonStudio.Projects.ISourceFile file)
        {
            var id = DocumentId.CreateNewId(project.Id);
            OnDocumentAdded(DocumentInfo.Create(id, file.Name, filePath: file.FilePath, loader: new FileTextLoader(file.FilePath, System.Text.Encoding.UTF8)));

            return id;
        }

        public DocumentId GetDocumentId(AvalonStudio.Projects.ISourceFile file)
        {
            var ids = CurrentSolution.GetDocumentIdsWithFilePath(file.Location);

            if (ids.Length != 1)
            {
                throw new NotImplementedException();
            }

            return ids.First();
        }

        public Document GetDocument(AvalonStudio.Projects.ISourceFile file)
        {
            var documentId = GetDocumentId(file);

            return CurrentSolution.GetDocument(documentId);
        }

        public void OpenDocument(AvalonStudio.Projects.ISourceFile file, AvalonEditTextContainer textContainer, Action<DiagnosticsUpdatedArgs> onDiagnosticsUpdated)
        {
            var documentId = GetDocumentId(file);

            OnDocumentOpened(documentId, textContainer);
            OnDocumentContextUpdated(documentId);

            _diagnosticsUpdatedNotifiers[documentId] = onDiagnosticsUpdated;
            _openDocumentTextLoaders.Add(documentId, textContainer);
        }

        public void CloseDocument(AvalonStudio.Projects.ISourceFile file)
        {
            var documentId = GetDocumentId(file);
            var textContainer = _openDocumentTextLoaders[documentId];

            _openDocumentTextLoaders.Remove(documentId);
            _diagnosticsUpdatedNotifiers.Remove(documentId, out Action<DiagnosticsUpdatedArgs> value);

            OnDocumentClosed(documentId, TextLoader.From(textContainer, VersionStamp.Default));
        }

        public new void SetCurrentSolution(Solution solution)
        {
            var oldSolution = CurrentSolution;
            var newSolution = base.SetCurrentSolution(solution);
            RaiseWorkspaceChangedEventAsync(WorkspaceChangeKind.SolutionChanged, oldSolution, newSolution);
        }

        public override bool CanOpenDocuments => true;

        public override bool CanApplyChange(ApplyChangesKind feature)
        {
            switch (feature)
            {
                case ApplyChangesKind.ChangeDocument:
                    return true;
                default:
                    return false;
            }
        }

        protected override void Dispose(bool finalize)
        {
            base.Dispose(finalize);
        }

        protected override void ApplyDocumentTextChanged(DocumentId document, SourceText newText)
        {
            _openDocumentTextLoaders[document].UpdateText(newText);
            
            OnDocumentTextChanged(document, newText, PreservationMode.PreserveIdentity);
        }

        public new void ClearSolution()
        {
            base.ClearSolution();
        }

        internal void ClearOpenDocument(DocumentId documentId)
        {
            base.ClearOpenDocument(documentId);
        }

        internal new void RegisterText(SourceTextContainer textContainer)
        {
            base.RegisterText(textContainer);
        }

        internal new void UnregisterText(SourceTextContainer textContainer)
        {
            base.UnregisterText(textContainer);
        }

        private class DirectiveInfo
        {
            public MetadataReference MetadataReference { get; }

            public bool IsActive { get; set; }

            public DirectiveInfo(MetadataReference metadataReference)
            {
                MetadataReference = metadataReference;
                IsActive = true;
            }
        }
    }
}