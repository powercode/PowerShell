// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PSTests.Parallel
{
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class ParameterBindingIntegrationTests
    {
        [Fact]
        public void NamedParameter_ExactMatch_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                        $Path
                    }
                    Test-Func -Path 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_PrefixMatch_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                        $Path
                    }
                    Test-Func -Pa 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_AmbiguousPrefix_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path, [string]$Process)
                    }
                    Test-Func -P 'value'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
                Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
                Assert.Contains("ambiguous", ps.Streams.Error[0].Exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void NamedParameter_Alias_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([Alias('CN')][string]$ComputerName)
                        $ComputerName
                    }
                    Test-Func -CN 'server1'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("server1", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_NotFound_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                    }
                    Test-Func -NonExistent 'value'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
                Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
            }
        }

        [Fact]
        public void NamedParameter_CaseInsensitive_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                        $Path
                    }
                    Test-Func -path 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParameter_AlreadyBound_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Path)
                    }
                    Test-Func -Path 'first' -Path 'second'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
                Assert.IsAssignableFrom<ParameterBindingException>(ps.Streams.Error[0].Exception);
            }
        }

        [Fact]
        public void SwitchParameter_NoArgument_IsPresent()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([switch]$Force)
                        $Force.IsPresent
                    }
                    Test-Func -Force
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(true, results[0].BaseObject);
            }
        }

        [Fact]
        public void SwitchParameter_ExplicitFalse_NotPresent()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([switch]$Force)
                        $Force.IsPresent
                    }
                    Test-Func -Force:$false
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(false, results[0].BaseObject);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class PositionalBindingIntegrationTests
    {
        [Fact]
        public void Positional_SingleSet_BindsByPosition()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$First,
                            [Parameter(Position=1)] [string]$Second
                        )
                        ""$First-$Second""
                    }
                    Test-Func 'hello' 'world'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello-world", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_SkippedPosition_BindsCorrectly()
        {
            using (var ps = PowerShell.Create())
            {
                // Position=0 and Position=2 — passing two args binds to 0 and 2 in order.
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$A,
                            [Parameter(Position=2)] [string]$C
                        )
                        ""$A-$C""
                    }
                    Test-Func 'first' 'third'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("first-third", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_NamedThenPositional_FillsRemaining()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$First,
                            [Parameter(Position=1)] [string]$Second
                        )
                        ""$First-$Second""
                    }
                    Test-Func -Second 'world' 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello-world", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_VRACaptures_Overflow()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [Parameter(Position=0)] [string]$First,
                            [Parameter(ValueFromRemainingArguments=$true)] [string[]]$Rest
                        )
                        [PSCustomObject]@{ First=$First; Rest=$Rest }
                    }
                    $r = Test-Func 'a' 'b' 'c'
                    $r.First + '-' + ($r.Rest -join ',')
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("a-b,c", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_MultiSet_ParamAppearsInBothSets_BindsOnce()
        {
            // Regression test for issue #2212:
            // A parameter that appears at different positions in different parameter sets
            // should only be bound once (to the first matching position).
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding(DefaultParameterSetName='One')]
                        param(
                            [Parameter(ParameterSetName='One', Position=0)]
                            [Parameter(ParameterSetName='Two', Position=1)]
                            [string]$First,
                            [Parameter(ParameterSetName='One', Position=1)]
                            [Parameter(ParameterSetName='Two', Position=0)]
                            [string]$Second
                        )
                        ""$First|$Second""
                    }
                    Test-Func 'Hello' 'World'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                // In set 'One' (default): First=pos0, Second=pos1 → "Hello|World"
                Assert.Equal("Hello|World", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_ExplicitSetSelection_UsesSetPositions()
        {
            // Selecting set 'Two' via a set-differentiating named parameter.
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding(DefaultParameterSetName='One')]
                        param(
                            [Parameter(ParameterSetName='One', Position=0)]
                            [Parameter(ParameterSetName='Two', Position=1)]
                            [string]$First,
                            [Parameter(ParameterSetName='One', Position=1)]
                            [Parameter(ParameterSetName='Two', Position=0)]
                            [string]$Second,
                            [Parameter(ParameterSetName='Two', Mandatory=$false)]
                            [switch]$UseTwo
                        )
                        ""$First|$Second""
                    }
                    Test-Func -UseTwo 'Hello' 'World'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                // In set 'Two': Second=pos0, First=pos1 → First='World', Second='Hello'
                Assert.Equal("World|Hello", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void Positional_Overflow_NoVRA_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([Parameter(Position=0)] [string]$A)
                        $A
                    }
                    Test-Func 'first' 'extra'
                ");
                var results = ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class ArgumentReparsingIntegrationTests
    {
        [Fact]
        public void Switch_NoValue_DefaultsToPresent()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([switch]$Enabled)
                        $Enabled.IsPresent
                    }
                    Test-Func -Enabled
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(true, results[0].BaseObject);
            }
        }

        [Fact]
        public void Switch_ExplicitFalse_IsNotPresent()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([switch]$Enabled)
                        $Enabled.IsPresent
                    }
                    Test-Func -Enabled:$false
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(false, results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParam_WithValue_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Name, [switch]$Verbose2, [string]$Other)
                        ""$Name|$Other|$($Verbose2.IsPresent)""
                    }
                    Test-Func -Name 'alpha' -Verbose2 -Other 'beta'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("alpha|beta|True", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void NamedParam_MissingValue_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Name)
                        $Name
                    }
                    Test-Func -Name
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void NamedParam_FollowedByAnotherParam_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Name, [string]$Other)
                        $Name
                    }
                    Test-Func -Name -Other
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void NamedParam_ColonSyntax_Binds()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Name)
                        $Name
                    }
                    Test-Func -Name:'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("hello", (string)results[0].BaseObject);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class TypeCoercionIntegrationTests
    {
        [Fact]
        public void String_Coerced_To_Int_Parameter()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([int]$Value)
                        $Value
                    }
                    Test-Func -Value '42'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(42, results[0].BaseObject);
            }
        }

        [Fact]
        public void Int_Coerced_To_String_Parameter()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string]$Value)
                        $Value
                    }
                    Test-Func -Value 99
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("99", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void UnconvertibleValue_WritesError()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([int]$Value)
                        $Value
                    }
                    Test-Func -Value 'not-a-number'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void Single_Value_Coerced_To_Array_Parameter()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([string[]]$Values)
                        $Values.Count
                    }
                    Test-Func -Values 'hello'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(1, results[0].BaseObject);
            }
        }

        [Fact]
        public void Enum_String_Coerced_To_Enum_Parameter()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param([System.IO.FileMode]$Mode)
                        [int]$Mode
                    }
                    Test-Func -Mode 'Open'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal((int)System.IO.FileMode.Open, results[0].BaseObject);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Validation integration tests — validation attributes fire through full
    // PowerShell parameter binding pipeline.
    // -----------------------------------------------------------------------
    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class ValidationIntegrationTests
    {
        [Fact]
        public void ValidateRange_AcceptsValueInRange()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateRange(1, 10)]
                            [int]$Value
                        )
                        $Value
                    }
                    Test-Func -Value 5
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(5, results[0].BaseObject);
            }
        }

        [Fact]
        public void ValidateRange_RejectsValueOutOfRange()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateRange(1, 10)]
                            [int]$Value
                        )
                        $Value
                    }
                    Test-Func -Value 99
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void ValidateSet_AcceptsValueInSet()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateSet('Red', 'Green', 'Blue')]
                            [string]$Color
                        )
                        $Color
                    }
                    Test-Func -Color 'Green'
                ");
                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("Green", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void ValidateSet_RejectsValueNotInSet()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateSet('Red', 'Green', 'Blue')]
                            [string]$Color
                        )
                        $Color
                    }
                    Test-Func -Color 'Yellow'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void ValidatePattern_RejectsNonMatchingString()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidatePattern('^\d+$')]
                            [string]$Value
                        )
                        $Value
                    }
                    Test-Func -Value 'abc'
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void ValidateNotNullOrEmpty_RejectsEmptyString()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Func {
                        [CmdletBinding()]
                        param(
                            [ValidateNotNullOrEmpty()]
                            [string]$Value
                        )
                        $Value
                    }
                    Test-Func -Value ''
                ");
                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class ConstrainedLanguageBindingTests
    {
        [Fact]
        public void ConstrainedLanguage_CustomTypeConversion_TrustedCmdlet_Succeeds()
        {
            var initialSessionState = InitialSessionState.CreateDefault2();
            initialSessionState.LanguageMode = PSLanguageMode.ConstrainedLanguage;

            using (var runspace = RunspaceFactory.CreateRunspace(initialSessionState))
            using (var ps = PowerShell.Create())
            {
                runspace.Open();
                ps.Runspace = runspace;
                ps.AddScript(@"
                    (Get-Date -Date '2024-01-02').Year
                ");

                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(2024, results[0].BaseObject);
            }
        }

        [Fact]
        public void ConstrainedLanguage_CustomTypeConversion_UntrustedCmdlet_Throws()
        {
            var initialSessionState = InitialSessionState.CreateDefault2();
            initialSessionState.LanguageMode = PSLanguageMode.ConstrainedLanguage;

            using (var runspace = RunspaceFactory.CreateRunspace(initialSessionState))
            using (var ps = PowerShell.Create())
            {
                runspace.Open();
                ps.Runspace = runspace;
                ps.AddScript(@"
                    function Test-Untrusted {
                        [CmdletBinding()]
                        param([string]$Path)

                        [System.IO.FileInfo]::new($Path)
                    }

                    Test-Untrusted -Path 'a.txt'
                ");

                ps.Invoke();
                Assert.NotEmpty(ps.Streams.Error);
            }
        }

        [Fact]
        public void ArgumentTransformation_OptionalArgWithExplicitNull()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-OptionalArg {
                        [CmdletBinding()]
                        param([AllowNull()] [string]$Name = 'default')

                        if ($null -eq $Name) { 'null' } else { $Name }
                    }

                    Test-OptionalArg -Name $null
                ");

                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(string.Empty, (string)results[0].BaseObject);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class ArrayParameterBindingTests
    {
        [Fact]
        public void ArrayToParameter_ExplicitArray_BindsCorrectly()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Array {
                        [CmdletBinding()]
                        param([string[]]$Items)
                        $Items -join ','
                    }

                    Test-Array -Items @('a','b')
                ");

                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal("a,b", (string)results[0].BaseObject);
            }
        }

        [Fact]
        public void ArrayToParameter_MultipleArgs_CombinedIntoArray()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    function Test-Array {
                        [CmdletBinding()]
                        param([string[]]$Items)
                        $Items.Count
                    }

                    Test-Array -Items 'a','b','c'
                ");

                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Single(results);
                Assert.Equal(3, results[0].BaseObject);
            }
        }
    }

    [Trait("Category", "ParameterBinding")]
    [Trait("Category", "Integration")]
    public class PipelineVariableTests
    {
        [Fact]
        public void PipelineVariable_SetAndAccessible_AcrossPipeline()
        {
            using (var ps = PowerShell.Create())
            {
                ps.AddScript(@"
                    1..3 |
                        ForEach-Object -PipelineVariable pv { $_ } |
                        ForEach-Object { '{0}:{1}' -f $pv, $_ }
                ");

                var results = ps.Invoke();
                Assert.Empty(ps.Streams.Error);
                Assert.Equal(3, results.Count);
                Assert.Equal("1:1", (string)results[0].BaseObject);
                Assert.Equal("2:2", (string)results[1].BaseObject);
                Assert.Equal("3:3", (string)results[2].BaseObject);
            }
        }
    }
}
