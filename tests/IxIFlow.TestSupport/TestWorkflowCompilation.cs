using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;

namespace IxIFlow.Tests.Infrastructure;

public static class TestWorkflowCompilation
{
    public static async Task<WorkflowDefinition> BuildAsync(WorkflowDocument document, WorkflowSourceOptions options)
    {
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options));

        if (!validation.IsValid)
        {
            var error = validation.Diagnostics.First(item => item.Severity == WorkflowDiagnosticSeverity.Error);
            var location = DocumentModel.GetLocation(document, error.Path);

            throw new WorkflowSourceException(error.Message, "test.yaml", location.Line, location.Column);
        }

        return Build(WorkflowSourceTranslator.Translate(document, "test.yaml", options));
    }

    public static WorkflowDefinition Build(string source)
    {
        var emitted = WorkflowCompilation.Compile(source);

        if (!emitted.Success)
            throw new WorkflowSourceException(string.Join("; ", emitted.Diagnostics.Select(item => item.Message)),
                emitted.Diagnostics[0].Path, emitted.Diagnostics[0].Line, emitted.Diagnostics[0].Column);

        return WorkflowCompilation.BuildAssembly(emitted.Assembly, emitted.Pdb);
    }
}