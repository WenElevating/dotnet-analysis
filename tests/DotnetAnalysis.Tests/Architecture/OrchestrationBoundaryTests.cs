using System.Reflection;
using System.Xml.Linq;

namespace DotnetAnalysis.Tests.Architecture;

/// <summary>
/// 验证诊断编排项目只依赖领域与应用契约，并且公共契约不暴露宿主或诊断基础设施类型。
/// </summary>
[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names describe the required project boundaries.")]
public sealed class OrchestrationBoundaryTests
{
    private static readonly string[] ExpectedProjectReferences =
    [
        @"..\DotnetAnalysis.Core\DotnetAnalysis.Core.csproj",
        @"..\DotnetAnalysis.Application\DotnetAnalysis.Application.csproj"
    ];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "DotnetAnalysis.Desktop",
        "DotnetAnalysis.Diagnostics",
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "Microsoft.Diagnostics"
    ];

    private static readonly string[] ForbiddenTypePrefixes =
    [
        "System.Windows",
        "Microsoft.Diagnostics",
        "DotnetAnalysis.Desktop",
        "DotnetAnalysis.Diagnostics"
    ];

    /// <summary>
    /// 验证项目文件只声明 Core 与 Application 项目引用，避免编排层反向依赖宿主或诊断实现。
    /// </summary>
    [TestMethod]
    public void ProjectReferences_OnlyPointToCoreAndApplication()
    {
        var projectPath = Path.Combine(GetSourceRoot(), "DotnetAnalysis.Orchestration", "DotnetAnalysis.Orchestration.csproj");
        var references = XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(static include => include is not null)
            .ToArray();

        CollectionAssert.AreEquivalent(
            ExpectedProjectReferences,
            references!);
    }

    /// <summary>
    /// 验证已编译程序集没有 WPF 或 Diagnostics 程序集引用，保证骨架阶段的依赖边界可被运行时检查。
    /// </summary>
    [TestMethod]
    public void AssemblyReferences_DoNotIncludeHostOrInfrastructureAssemblies()
    {
        var assembly = Assembly.Load("DotnetAnalysis.Orchestration");
        var referencedAssemblyNames = assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToArray();

        foreach (var forbiddenPrefix in ForbiddenAssemblyPrefixes)
        {
            Assert.IsFalse(
                referencedAssemblyNames.Any(name => name.StartsWith(forbiddenPrefix, StringComparison.Ordinal)),
                $"Orchestration must not reference assembly '{forbiddenPrefix}'.");
        }
    }

    /// <summary>
    /// 验证编排程序集的公共类型签名不泄漏 WPF、Diagnostics 或其他宿主基础设施类型。
    /// </summary>
    [TestMethod]
    public void PublicContracts_DoNotExposeHostOrInfrastructureTypes()
    {
        var assembly = Assembly.Load("DotnetAnalysis.Orchestration");
        var publicTypes = assembly.GetExportedTypes();

        foreach (var type in publicTypes)
        {
            AssertTypeIsAllowed(type, $"public type {type.FullName}");
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                AssertTypeIsAllowed(member.DeclaringType, $"member declaring type {member.Name}");
                if (member is MethodInfo method)
                {
                    AssertTypeIsAllowed(method.ReturnType, $"return type {method.Name}");
                    foreach (var parameter in method.GetParameters())
                    {
                        AssertTypeIsAllowed(parameter.ParameterType, $"parameter type {method.Name}");
                    }
                }
                else if (member is PropertyInfo property)
                {
                    AssertTypeIsAllowed(property.PropertyType, $"property type {property.Name}");
                }
                else if (member is FieldInfo field)
                {
                    AssertTypeIsAllowed(field.FieldType, $"field type {field.Name}");
                }
            }
        }
    }

    private static void AssertTypeIsAllowed(Type? type, string description)
    {
        if (type is null)
        {
            return;
        }

        var typeName = type.FullName ?? type.Name;
        foreach (var forbiddenPrefix in ForbiddenTypePrefixes)
        {
            Assert.IsFalse(
                typeName.StartsWith(forbiddenPrefix, StringComparison.Ordinal),
                $"Orchestration {description} must not expose '{typeName}'.");
        }
    }

    private static string GetSourceRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src"));
}
