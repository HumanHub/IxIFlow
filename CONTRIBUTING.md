# Contributing to IxIFlow

Thanks for your interest in contributing to IxIFlow! We welcome contributions from developers of all skill levels.

## Getting Started

### Prerequisites

- .NET 8.0 or later
- Visual Studio 2022, VS Code, or JetBrains Rider
- Git

### Setting Up Your Development Environment

1. **Fork the repository** on GitHub
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/yourusername/IxIFlow.git
   cd IxIFlow
   ```
3. **Restore packages**:
   ```bash
   dotnet restore
   ```
4. **Build the solution**:
   ```bash
   dotnet build
   ```
5. **Run the tests** to make sure everything works:
   ```bash
   dotnet test
   ```

## Development Workflow

### Running Tests

We have comprehensivish test coverage. Run tests frequently while developing:

```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Run specific test class
dotnet test --filter "FluentApiTests"

# Run tests and generate coverage report
dotnet test --collect:"XPlat Code Coverage"
```

### Code Style

We follow standard C# conventions with a few specifics:

- **File Namespaces**: Use file-scoped namespaces where possible
- **Primary Constructors**: Use primary constructors for simple cases
- **Naming**: 
  - Use PascalCase for public members
  - Use camelCase for private fields
  - Use descriptive names (avoid abbreviations)
- **Comments**: 
  - Keep comments professional and relevant
  - Use XML documentation for public APIs
  - No emojis or unprofessional language in code comments

### Project Structure

```
IxIFlow/
├── IxIFlow/                    # Main library
│   ├── Builders/              # Workflow builder implementations
│   ├── Core/                  # Core engine and interfaces
│   └── Extensions/            # DI extensions
└── IxIFlow.Tests/             # Test projects
    ├── ExecutionTests/        # Runtime execution tests
    └── SyntaxTests/          # API syntax and compilation tests
```

## Contributing Code

### Types of Contributions We Welcome

- **Bug fixes**: Fix issues in existing functionality
- **New features**: Add new workflow capabilities
- **Performance improvements**: Make workflows run faster or use less memory
- **Documentation**: Improve README, code comments, or examples
- **Tests**: Add test coverage for existing features

### Before You Start

1. **Check existing issues** to see if someone is already working on it
2. **Create an issue** to discuss larger changes before implementing
3. **Keep changes focused**: One feature or fix per pull request

### Making Changes

1. **Create a feature branch** from main:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make your changes** following our coding standards

3. **Add tests** for any new functionality:
   - Unit tests for individual components
   - Integration tests for complete workflows
   - Syntax tests for new API features

4. **Update documentation** if needed:
   - Update README.md for new features
   - Add XML documentation for public APIs
   - Update code examples

5. **Test your changes**:
   ```bash
   dotnet test
   dotnet build --configuration Release
   ```

### Submitting Your Pull Request

1. **Push your branch** to your fork:
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Create a pull request** on GitHub with:
   - Clear title describing what you changed
   - Description explaining why the change is needed
   - Reference to any related issues
   - Screenshots if the change affects examples or documentation

3. **Respond to feedback** from maintainers promptly

## Examples and Testing

### Writing Good Tests

When adding new features, include comprehensive tests:

```csharp
[Fact]
public async Task YourNewFeature_ValidInput_ShouldWork()
{
    // Arrange - Set up test data and mocks
    var workflowData = new TestWorkflowData { /* test data */ };
    
    // Act - Execute the feature
    var result = await ExecuteWorkflow(workflowData);
    
    // Assert - Verify the results
    Assert.True(result.IsSuccess);
    Assert.Equal(expectedValue, result.ActualValue);
}
```

### Writing Good Examples

When adding new features, include clear examples in the README:

```csharp
// Show the simplest possible usage
.Step<YourNewActivity>(setup => setup
    .Input(step => step.InputProperty).From(data => data.WorkflowData.Source)
    .Output(step => step.OutputProperty).To(data => data.WorkflowData.Target))
```

## Reporting Issues

### Bug Reports

Include these details when reporting bugs:

- **What happened**: Clear description of the issue
- **What you expected**: What should have happened instead
- **Steps to reproduce**: Minimal code example that shows the problem
- **Environment**: .NET version, operating system, etc.
- **Logs/Errors**: Any error messages or stack traces

### Feature Requests

For new feature ideas:

- **Use case**: Why do you need this feature?
- **Current workaround**: How do you handle this now?
- **Proposed solution**: What would the API look like?
- **Examples**: Show how you'd use the new feature

## Community Guidelines

### Be Respectful

- Use welcoming and inclusive language
- Respect different viewpoints and experiences
- Accept constructive criticism gracefully
- Focus on what's best for the community

### Be Helpful

- Help other contributors when you can
- Share knowledge and experience
- Provide clear, actionable feedback
- Be patient with new contributors

## Release Process

Maintainers handle releases, but here's how it works:

1. **Version bumping**: We follow semantic versioning (semver)
2. **Release notes**: Document all changes in releases
3. **NuGet publishing**: Packages are published automatically via CI/CD

## Questions?

- **GitHub Issues**: For bugs and feature requests
- **Discussions**: For questions about usage or architecture
- **Email**: For security issues or private matters

## Recognition

Contributors are recognized in:
- Release notes for their contributions
- GitHub contributor lists
- Special thanks for significant contributions

Thank you for contributing to IxIFlow!
