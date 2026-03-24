# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
Describe "Tests for parameter binding" -Tags "CI" {
    Context 'Test of Mandatory parameters' {
        BeforeAll {
            $f = "function get-foo { param([Parameter(mandatory=`$true)] `$a) `$a };"
        }

        It 'Mandatory parameters used in non-interactive host' {
            $rs = [system.management.automation.runspaces.runspacefactory]::CreateRunspace()
            $rs.open()
            $ps = [System.Management.Automation.PowerShell]::Create()
            $ps.Runspace = $rs

            try
            {
                [void] $ps.AddScript($f + "get-foo")
                $asyncResult = $ps.BeginInvoke()
                $ps.EndInvoke($asyncResult)

                $ps.Streams.Error.Count | Should -Be 1 # the host does not implement it.
                $ps.InvocationStateInfo.State | Should -BeExactly 'Completed'
            } finally {
                $ps.Dispose()
                $rs.Dispose()
            }
        }

        It 'Mandatory parameters used in interactive host' {
            $th = New-TestHost
            $rs = [system.management.automation.runspaces.runspacefactory]::Createrunspace($th)
            $rs.open()
            $ps = [System.Management.Automation.PowerShell]::Create()
            $ps.Runspace = $rs

            try
            {
                $ps.AddScript($f + "get-foo").invoke()
                $prompt = $th.ui.streams.prompt[0]
                $prompt | Should -Not -BeNullOrEmpty
                $result = $prompt.split(":")
                $result[0] | Should -Match 'get-foo'
                $result[-1] | Should -BeExactly 'a'
            } finally {
                $rs.Close()
                $rs.Dispose()
                $ps.Dispose()
            }
        }
    }

    It 'Test of positional parameters' {
        function get-foo
        {
            [CmdletBinding()]
            param($a)
            $a
        }

        get-foo a | Should -BeExactly 'a'
        get-foo -a b | Should -BeExactly 'b'
    }

    It 'Positional parameters when only one position specified: position = 1' {
        function get-foo
        {
            param([Parameter(position=1)] $a )
            $a
        }

        get-foo b | Should -BeExactly 'b'
    }

    It 'Positional parameters when only position specified: position = 2' {
        function get-foo
        {
            param([Parameter(position=2)] $a )
            $a
        }

        get-foo b | Should -BeExactly 'b'
    }

    It 'Multiple positional parameters case 1' {
        function get-foo
        {
            param( [Parameter(position=1)] $a,
                   [Parameter(position=2)] $b )
            $a; $b
        }

        ( get-foo c d ) -join ',' | Should -BeExactly 'c,d'
        ( get-foo -a c d ) -join ',' | Should -BeExactly 'c,d'
        ( get-foo -a c -b d ) -join ',' | Should -BeExactly 'c,d'
        ( get-foo -b d c ) -join ',' | Should -BeExactly 'c,d'
        ( get-foo c -b d ) -join ',' | Should -BeExactly 'c,d'
    }

    It 'Multiple positional parameters case 2:  the parameters are put in different order?' {
        function get-foo
        {
            # the parameters are purposefully out of order.
            param( [Parameter(position=2)] $a,
                   [Parameter(position=1)] $b )
            $a; $b
        }

        (get-foo c d) -join ',' | Should -BeExactly 'd,c'
    }

    It 'Value from pipeline' {
        function get-foo
        {
            param( [Parameter(valuefrompipeline=$true)] $a )
            process
            {
                if($a % 2 -eq 0)
                {
                    $a
                }
            }
        }

        (1..10 | get-foo) -join ',' | Should -BeExactly '2,4,6,8,10'
    }

    It 'Value from pipeline by property name' {
        function get-foo
        {
            param( [Parameter(valuefrompipelinebypropertyname=$true)] $foo )
            process
            {
                if($foo % 2 -eq 0)
                {
                    $foo
                }
            }
        }

        $b = 1..10 | Select-Object @{name='foo'; expression={$_ * 10}} | get-foo
        $b -join ',' | Should -BeExactly '10,20,30,40,50,60,70,80,90,100'
    }

    It 'Value from remaining arguments' {
        function get-foo
        {
            param(
                [Parameter(position=1)] $a,
                [Parameter(valuefromremainingarguments=$true)] $foo
            )
            $foo
        }

        ( get-foo a b c d ) -join ',' | Should -BeExactly 'b,c,d'
        ( get-foo a b -a c d ) -join ',' | Should -BeExactly 'a,b,d'
        ( get-foo a b -a c -q d ) -join ',' | Should -BeExactly 'a,b,-q,d'
    }

    It 'Multiple parameter sets with Value from remaining arguments' {
        function get-foo
        {
            param( [Parameter(parametersetname='set1',position=1)] $a,
                   [Parameter(parametersetname='set2',position=1)] $b,
                   [parameter(valuefromremainingarguments=$true)] $foo )
            $foo
        }

        { get-foo -a a -b b c d } | Should -Throw -ErrorId 'AmbiguousParameterSet,get-foo'
        ( get-foo -a a b c d ) -join ',' | Should -BeExactly 'b,c,d'
        ( get-foo -b b a c d ) -join ',' | Should -BeExactly 'a,c,d'
    }

    It 'Too many parameter sets defined' {
        $scriptblock = {
            param($numSets=1)
            $parameters = (1..($numSets) | ForEach-Object { "[Parameter(parametersetname='set$_')]`$a$_" }) -join ', '
            $body = "param($parameters) 'working'"
            $sb = [scriptblock]::Create($body)
            & $sb -a1 123
        }

        & $scriptblock -numSets 32 | Should -Be 'working'
        { & $scriptblock -numSets 33 } | Should -Throw -ErrorId 'ParsingTooManyParameterSets'
    }

    It 'Default parameter set with value from remaining arguments case 1' {
        function get-foo
        {
            [CmdletBinding(DefaultParameterSetName="set1")]
            param( [Parameter(parametersetname="set1", position=1)] $a,
                   [Parameter(parametersetname="set2", position=1)] $b,
                   [parameter(valuefromremainingarguments=$true)] $foo )
            $a,$b,$foo
        }

        $x,$y,$z=get-foo a b c d
        $x | Should -BeExactly 'a'
        $y | Should -BeNullOrEmpty
        $z -join ',' | Should -BeExactly 'b,c,d'
    }

    It 'Default parameter set with value from remaining argument case 2' {
        function get-foo
        {
            [CmdletBinding(DefaultParameterSetName="set2")]
            param( [Parameter(parametersetname="set1", position = 1)] $a,
                   [Parameter(parametersetname="set2", position = 1)] $b,
                   [parameter(valuefromremainingarguments=$true)] $foo )
            $a,$b,$foo
        }

        $x,$y,$z=get-foo a b c d

        $x | Should -BeNullOrEmpty
        $y | Should -BeExactly 'a'
        $z -join ',' | Should -BeExactly 'b,c,d'
    }

    It 'Alias are specified for parameters' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)][alias("foo", "bar")] $a )
            $a
        }

        get-foo -foo b | Should -BeExactly 'b'
    }

    It 'Invoking with script block' {
        $foo = . { param([Parameter(position=2)] $a, [Parameter(position=1)]$b); $a; $b} a b
        $foo[0] | Should -BeExactly 'b'
    }

    It 'Normal functions' {
        function foo ($a, $b) {$b, $a}
        ( foo a b ) -join ',' | Should -BeExactly 'b,a'
    }

    It 'Null is not Allowed when AllowNull attribute is not set' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)] $a )
            $a
        }

        { get-foo -a $null } | Should -Throw -ErrorId 'ParameterArgumentValidationErrorNullNotAllowed,get-foo'

    }

    It 'Null is allowed when Allownull attribute is set' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)][allownull()] $a )
            $a
        }

        (get-foo -a $null) | Should -BeNullOrEmpty

    }

    It 'Empty string is not allowed AllowEmptyString Attribute is not set' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)][string] $a )
            $a
        }

        { get-foo -a '' } | Should -Throw -ErrorId 'ParameterArgumentValidationErrorEmptyStringNotAllowed,get-foo'
    }

    It 'Empty string is allowed when AllowEmptyString Attribute is set' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)][allowemptystring()][string] $a )
            $a
        }

        get-foo -a '' | Should -BeExactly ''
    }

    It 'Empty collection is not allowed when AllowEmptyCollection it not set' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)][string[]] $a )
            $a
        }

        { get-foo -a @() } | Should -Throw -ErrorId 'ParameterArgumentValidationErrorEmptyArrayNotAllowed,get-foo'
    }

    It 'Empty collection is allowed when allowEmptyCollection is set' {
        function get-foo
        {
            param( [Parameter(mandatory=$true)][allowemptycollection()][string[]] $a )
            $a
        }

        get-foo -a @() | Should -BeNullOrEmpty
    }

    It 'Unspecified non-mandatory bool should not cause exception' {
        function get-foo
        {
            param([Parameter(Mandatory=$true, Position=0, ValueFromPipeline=$true)] $a,
                  [System.Boolean] $b)
            $a
        }

        42 | get-foo | Should -Be 42
    }

    It 'Parameter binding failure on Parameter1 should not cause parameter binding failure on Length' {
        function get-foo
        {
          param( [Parameter(ValueFromPipeline = $true)] [int] $Parameter1 = 10,
                 [Parameter(ValueFromPipelineByPropertyName = $true)] [int] $Length = 100 )
          process  { $Length }
        }

        'abc' | get-foo | Should -Be 3
    }

    It 'Binding array of string to array of bool should fail (cmdletbinding)' {
        function get-foo
        {
           [cmdletbinding()]
           param ([bool[]] $Parameter )
           $Parameter
        }

        { get-foo 'a','b' } | Should -Throw -ErrorId 'ParameterArgumentTransformationError,get-foo'
    }

    It "Binding array of string to array of bool should succeed" {
        function get-foo
        {
           param ([bool[]] $Parameter)
           $Parameter
        }

        $x = get-foo 'a','b'
        $x[0] | Should -BeTrue
        $x[1] | Should -BeTrue
    }

    Context 'Default value conversion tests' {
        It 'Parameter default value is converted correctly to the proper type when nothing is set on parameter' {
            function get-fooa
            {
                param( [System.Reflection.MemberTypes] $memberTypes = $([Enum]::GetNames("System.Reflection.MemberTypes") -join ",") )
                $memberTypes | Should -BeOfType System.Reflection.MemberTypes
            }

            get-fooa
        }

        It "Parameter default value is converted correctly to the proper type when CmdletBinding is set on param" {
            function get-foob
            {
                [CmdletBinding()]
                param( [System.Reflection.MemberTypes] $memberTypes = $([Enum]::GetNames("System.Reflection.MemberTypes") -join ",") )
                $memberTypes | Should -BeOfType System.Reflection.MemberTypes
            }

            get-foob
        }

        It "No default value specified should not cause error when parameter attribute is set on the parameter" {
            function get-fooc
            {
                param( [Parameter()] [System.Reflection.MemberTypes] $memberTypes )
                $memberTypes | Should -BeNullOrEmpty
            }

            get-fooc
        }

        It "No default value specified should not cause error when nothing is set on parameter" {
            function get-food
            {
                param( [System.Reflection.MemberTypes] $memberTypes )
                $memberTypes | Should -BeNullOrEmpty
            }

            get-food
        }

        It "Validation attributes should not run on default values when nothing is set on the parameter" {
            function get-fooe
            {
                param([ValidateRange(1,42)] $p = 55)
                $p
            }

            get-fooe | Should -Be 55
        }

        It "Validation attributes should not run on default values when CmdletBinding is set on the parameter" {
            function get-foof
            {
                [CmdletBinding()]
                param([ValidateRange(1,42)] $p = 55)
                $p
            }

            get-foof | Should -Be 55
        }

        It "Validation attributes should not run on default values" {
            function get-foog
            {
                param([ValidateRange(1,42)] $p)
                $p
            }

            { get-foog } | Should -Not -Throw
        }

        It "Validation attributes should not run on default values when CmdletBinding is set" {
            function get-fooh
            {
                [CmdletBinding()]
                param([ValidateRange(1,42)] $p)
                $p
            }

            { get-fooh } | Should -Not -Throw
        }

        It "ValidateScript can use custom ErrorMessage" {
            function get-fooi {
                [CmdletBinding()]
                param([ValidateScript({$_ -gt 2}, ErrorMessage = "Item '{0}' failed '{1}' validation")] $p)
                $p
            }

            $err = { get-fooi -p 2 } | Should -Throw -ErrorId 'ParameterArgumentValidationError,get-fooi' -PassThru
            $err.Exception.Message | Should -BeExactly "Cannot validate argument on parameter 'p'. Item '2' failed '`$_ -gt 2' validation"
        }

        It "ValidatePattern can use custom ErrorMessage" {
            function get-fooj
            {
                [CmdletBinding()]
                param([ValidatePattern("\s+", ErrorMessage = "Item '{0}' failed '{1}' regex")] $p)
                $p
            }

            $err = { get-fooj -p 2 } | Should -Throw -ErrorId 'ParameterArgumentValidationError,get-fooj' -PassThru
            $err.Exception.Message | Should -BeExactly "Cannot validate argument on parameter 'p'. Item '2' failed '\s+' regex"
        }

        It "ValidateSet can use custom ErrorMessage" {
            function get-fook
            {
                param([ValidateSet('A', 'B', 'C', IgnoreCase=$false, ErrorMessage="Item '{0}' is not in '{1}'")] $p)
            }

            $err = { get-fook -p 2 } | Should -Throw -ErrorId 'ParameterArgumentValidationError,get-fook' -PassThru
            $set = 'A','B','C' -join [Globalization.CultureInfo]::CurrentUICulture.TextInfo.ListSeparator
            $err.Exception.Message | Should -BeExactly "Cannot validate argument on parameter 'p'. Item '2' is not in '$set'"
        }

    }

    #known issue 2069
    It 'Some conversions should be attempted before trying to encode a collection' -Skip:$IsCoreCLR {
        try {
                 $null = [Test.Language.ParameterBinding.MyClass]
            }
            catch {
                Add-Type -PassThru -TypeDefinition @'
                using System.Management.Automation;
                using System;
                using System.Collections;
                using System.Collections.ObjectModel;
                using System.IO;

                namespace Test.Language.ParameterBinding {
                    public class MyClass : Collection<string>
                    {
                        public MyClass() {}
                        public MyClass(Hashtable h) {}
                    }

                    [Cmdlet("Get", "TestCmdlet")]
                    public class MyCmdlet : PSCmdlet {
                        [Parameter]
                        public MyClass MyParameter
                        {
                            get { return myParameter; }
                            set { myParameter = value; }
                        }
                        private MyClass myParameter;

                        protected override void ProcessRecord()
                        {
                            WriteObject((myParameter == null) ? "<null>" : "hashtable");
                        }
                    }
                }
'@ | ForEach-Object {$_.assembly} | Import-Module
            }

        Get-TestCmdlet -MyParameter @{ a = 42 } | Should -BeExactly 'hashtable'
    }

    It 'Parameter pasing is consuming enumerators' {
        $a = 1..4
        $b = $a.getenumerator()
        $null = $b.MoveNext()
        $null = $b.current
        & { } $b

        #The position of the enumerator shouldn't be modified
        $b.current | Should -Be 1
    }

    Context 'Positional parameter binding across multiple parameter sets (issue #2212)' {
        It 'Parameter with different positions across parameter sets binds correctly' {
            function Test-PositionalBinding2212 {
                [CmdletBinding()]
                param(
                    [Parameter(ParameterSetName = 'Two', Position = 0)]
                    [Parameter(ParameterSetName = 'One', Position = 1)]
                    [string] $First,

                    [Parameter(ParameterSetName = 'Two', Position = 1)]
                    [string] $Second
                )

                @{
                    ParameterSet = $PSCmdlet.ParameterSetName
                    First = $First
                    Second = $Second
                }
            }

            $result = Test-PositionalBinding2212 Hello World
            $result.ParameterSet | Should -BeExactly 'Two'
            $result.First | Should -BeExactly 'Hello'
            $result.Second | Should -BeExactly 'World'
        }

        It 'PSBoundParameters correctly reflects positional binding across parameter sets' {
            function Test-BoundParams2212 {
                [CmdletBinding()]
                param(
                    [Parameter(ParameterSetName = 'Two', Position = 0)]
                    [Parameter(ParameterSetName = 'One', Position = 1)]
                    [string] $First,

                    [Parameter(ParameterSetName = 'Two', Position = 1)]
                    [string] $Second
                )

                $PSBoundParameters
            }

            $bound = Test-BoundParams2212 Hello World
            $bound['First'] | Should -BeExactly 'Hello'
            $bound['Second'] | Should -BeExactly 'World'
            $bound.Count | Should -Be 2
        }

        It 'Named parameter binding still works when parameter has different positions across sets' {
            function Test-NamedBinding2212 {
                [CmdletBinding()]
                param(
                    [Parameter(ParameterSetName = 'Two', Position = 0)]
                    [Parameter(ParameterSetName = 'One', Position = 1)]
                    [string] $First,

                    [Parameter(ParameterSetName = 'Two', Position = 1)]
                    [string] $Second
                )

                @{
                    ParameterSet = $PSCmdlet.ParameterSetName
                    First = $First
                    Second = $Second
                }
            }

            $result = Test-NamedBinding2212 -First Hello -Second World
            $result.ParameterSet | Should -BeExactly 'Two'
            $result.First | Should -BeExactly 'Hello'
            $result.Second | Should -BeExactly 'World'
        }

        It 'Parameter at same numeric position across sets does not double-bind' {
            # Regression: Position=5 in SetA and Position=5 in SetB caused the same
            # double-binding symptom as the primary issue.
            function Test-PositionalSamePosition {
                [CmdletBinding()]
                param(
                    [Parameter(ParameterSetName = 'SetA', Position = 0)]
                    [Parameter(ParameterSetName = 'SetB', Position = 5)]
                    [string] $Alpha,

                    [Parameter(ParameterSetName = 'SetA', Position = 5)]
                    [string] $Beta
                )

                @{
                    ParameterSet = $PSCmdlet.ParameterSetName
                    Alpha = $Alpha
                    Beta = $Beta
                }
            }

            $result = Test-PositionalSamePosition Foo Bar
            $result.ParameterSet | Should -BeExactly 'SetA'
            $result.Alpha | Should -BeExactly 'Foo'
            $result.Beta | Should -BeExactly 'Bar'
        }

        It 'Parameter at different positions across three parameter sets binds correctly' {
            function Test-ThreeSetPositional {
                [CmdletBinding()]
                param(
                    [Parameter(ParameterSetName = 'A', Position = 0)]
                    [Parameter(ParameterSetName = 'B', Position = 1)]
                    [Parameter(ParameterSetName = 'C', Position = 2)]
                    [string] $Param1,

                    [Parameter(ParameterSetName = 'A', Position = 1)]
                    [string] $Param2,

                    [Parameter(ParameterSetName = 'A', Position = 2)]
                    [string] $Param3
                )

                @{
                    ParameterSet = $PSCmdlet.ParameterSetName
                    Param1 = $Param1
                    Param2 = $Param2
                    Param3 = $Param3
                }
            }

            $result = Test-ThreeSetPositional X Y Z
            $result.ParameterSet | Should -BeExactly 'A'
            $result.Param1 | Should -BeExactly 'X'
            $result.Param2 | Should -BeExactly 'Y'
            $result.Param3 | Should -BeExactly 'Z'
        }
    }

    Context 'Named parameter matching edge cases' {
        BeforeAll {
            function Test-NamedMatchExact {
                [CmdletBinding()]
                param(
                    [string] $Path,
                    [string] $PathInfo
                )
                @{ Path = $Path; PathInfo = $PathInfo }
            }

            function Test-NamedMatchPrefix {
                [CmdletBinding()]
                param(
                    [string] $Path
                )
                $Path
            }

            function Test-NamedMatchAmbiguous {
                [CmdletBinding()]
                param(
                    [string] $Path,
                    [string] $Process
                )
                @{ Path = $Path; Process = $Process }
            }

            function Test-NamedMatchAlias {
                [CmdletBinding()]
                param(
                    [Alias('CN')]
                    [string] $ComputerName
                )
                $ComputerName
            }

            function Test-NamedMatchAliasVsPrefix {
                [CmdletBinding()]
                param(
                    [Alias('CN')]
                    [string] $ComputerName,
                    [string] $Content
                )
                @{ ComputerName = $ComputerName; Content = $Content }
            }
        }

        It 'Exact match takes precedence over prefix match when both exist' {
            $result = Test-NamedMatchExact -Path 'p1' -PathInfo 'p2'
            $result.Path | Should -BeExactly 'p1'
            $result.PathInfo | Should -BeExactly 'p2'
        }

        It 'Prefix match works with minimum unique characters' {
            $result = Test-NamedMatchPrefix -Pa 'hello'
            $result | Should -BeExactly 'hello'
        }

        It 'Ambiguous prefix throws AmbiguousParameter error' {
            { Test-NamedMatchAmbiguous -P 'val' } | Should -Throw -ErrorId 'AmbiguousParameter,Test-NamedMatchAmbiguous'
        }

        It 'Alias match works' {
            $result = Test-NamedMatchAlias -CN 'server1'
            $result | Should -BeExactly 'server1'
        }

        It 'Alias exact match takes precedence over parameter prefix match' {
            # -CN is an exact alias match; -Co would be an ambiguous prefix if both ComputerName and Content are present
            $result = Test-NamedMatchAliasVsPrefix -CN 'server1' -Content 'data'
            $result.ComputerName | Should -BeExactly 'server1'
            $result.Content | Should -BeExactly 'data'
        }

        It 'Case-insensitive matching works' {
            $result = Test-NamedMatchPrefix -path 'hello'
            $result | Should -BeExactly 'hello'
        }

        It 'Case-insensitive prefix match works' {
            # -PA (uppercase) also prefix-matches Path
            $result = Test-NamedMatchPrefix -PA 'hello'
            $result | Should -BeExactly 'hello'
        }

        It 'Parameter not found throws NamedParameterNotFound error' {
            { Test-NamedMatchPrefix -NonExistent 'val' } | Should -Throw -ErrorId 'NamedParameterNotFound,Test-NamedMatchPrefix'
        }

        It 'Parameter already bound throws ParameterAlreadyBound error' {
            { Test-NamedMatchPrefix -Path 'a' -Path 'b' } | Should -Throw -ErrorId 'ParameterAlreadyBound,Test-NamedMatchPrefix'
        }
    }

    Context 'Positional parameter binding edge cases' {
        It 'Positional parameter in one set but not another — positional binding selects the set with a position' {
            function Test-PositionalOneSet {
                [CmdletBinding(DefaultParameterSetName = 'SetA')]
                param(
                    [Parameter(ParameterSetName = 'SetA', Position = 0)]
                    [Parameter(ParameterSetName = 'SetB')]
                    [string] $Alpha,

                    [Parameter(ParameterSetName = 'SetB', Mandatory = $true)]
                    [string] $Discriminator
                )
                @{ ParameterSet = $PSCmdlet.ParameterSetName; Alpha = $Alpha }
            }

            $result = Test-PositionalOneSet 'hello'
            $result.ParameterSet | Should -BeExactly 'SetA'
            $result.Alpha | Should -BeExactly 'hello'
        }

        It 'Position gaps (0, 2, 5) — arguments fill positions in ascending position order' {
            function Test-PositionGaps {
                [CmdletBinding()]
                param(
                    [Parameter(Position = 0)] [string] $A,
                    [Parameter(Position = 2)] [string] $B,
                    [Parameter(Position = 5)] [string] $C
                )
                @{ A = $A; B = $B; C = $C }
            }

            $result = Test-PositionGaps 'alpha' 'bravo' 'charlie'
            $result.A | Should -BeExactly 'alpha'
            $result.B | Should -BeExactly 'bravo'
            $result.C | Should -BeExactly 'charlie'
        }

        It 'All-sets positional parameter (no ParameterSetName) works with any parameter set' {
            function Test-AllSetsPositional {
                [CmdletBinding(DefaultParameterSetName = 'SetA')]
                param(
                    [Parameter(Position = 0)]
                    [string] $Common,

                    [Parameter(ParameterSetName = 'SetA')]
                    [switch] $UseA,

                    [Parameter(ParameterSetName = 'SetB')]
                    [switch] $UseB
                )
                @{ ParameterSet = $PSCmdlet.ParameterSetName; Common = $Common }
            }

            $resultA = Test-AllSetsPositional 'hello' -UseA
            $resultA.Common | Should -BeExactly 'hello'
            $resultA.ParameterSet | Should -BeExactly 'SetA'

            $resultB = Test-AllSetsPositional 'world' -UseB
            $resultB.Common | Should -BeExactly 'world'
            $resultB.ParameterSet | Should -BeExactly 'SetB'
        }

        It 'Named parameter binding followed by positional — positional fills remaining unbound slot' {
            function Test-NamedThenPositional {
                [CmdletBinding()]
                param(
                    [Parameter(Position = 0)] [string] $First,
                    [Parameter(Position = 1)] [string] $Second
                )
                @{ First = $First; Second = $Second }
            }

            $result = Test-NamedThenPositional -Second 'two' 'one'
            $result.First | Should -BeExactly 'one'
            $result.Second | Should -BeExactly 'two'
        }

        It 'Default parameter set is preferred when two sets have a parameter at the same position' {
            function Test-DefaultSetPreference {
                [CmdletBinding(DefaultParameterSetName = 'Alpha')]
                param(
                    [Parameter(ParameterSetName = 'Alpha', Position = 0)]
                    [string] $AlphaValue,

                    [Parameter(ParameterSetName = 'Beta', Position = 0)]
                    [string] $BetaValue
                )
                @{ ParameterSet = $PSCmdlet.ParameterSetName; AlphaValue = $AlphaValue; BetaValue = $BetaValue }
            }

            $result = Test-DefaultSetPreference 'test'
            $result.ParameterSet | Should -BeExactly 'Alpha'
            $result.AlphaValue | Should -BeExactly 'test'
        }

        It 'Named parameter selects set, then positional binds within that set' {
            function Test-SetThenPositional {
                [CmdletBinding()]
                param(
                    [Parameter(ParameterSetName = 'SetA', Position = 0)]
                    [string] $Alpha,

                    [Parameter(ParameterSetName = 'SetA')]
                    [switch] $UseA,

                    [Parameter(ParameterSetName = 'SetB', Position = 0)]
                    [string] $Beta,

                    [Parameter(ParameterSetName = 'SetB')]
                    [switch] $UseB
                )
                @{ ParameterSet = $PSCmdlet.ParameterSetName; Alpha = $Alpha; Beta = $Beta }
            }

            $resultA = Test-SetThenPositional -UseA 'hello'
            $resultA.ParameterSet | Should -BeExactly 'SetA'
            $resultA.Alpha | Should -BeExactly 'hello'

            $resultB = Test-SetThenPositional -UseB 'world'
            $resultB.ParameterSet | Should -BeExactly 'SetB'
            $resultB.Beta | Should -BeExactly 'world'
        }

        It 'ValueFromRemainingArguments does not steal from positional binding' {
            function Test-VRAOrder {
                [CmdletBinding()]
                param(
                    [Parameter(Position = 0)] [string] $Named,
                    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Rest
                )
                @{ Named = $Named; Rest = $Rest }
            }

            $result = Test-VRAOrder 'first' 'extra1' 'extra2'
            $result.Named | Should -BeExactly 'first'
            $result.Rest | Should -HaveCount 2
            $result.Rest[0] | Should -BeExactly 'extra1'
            $result.Rest[1] | Should -BeExactly 'extra2'
        }

        It 'Positional argument beyond number of positional parameters binds to ValueFromRemainingArguments' {
            function Test-PositionalOverflow {
                [CmdletBinding()]
                param(
                    [Parameter(Position = 0)] [string] $First,
                    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Rest
                )
                @{ First = $First; Rest = $Rest }
            }

            $result = Test-PositionalOverflow 'pos0' 'overflow1' 'overflow2'
            $result.First | Should -BeExactly 'pos0'
            $result.Rest | Should -HaveCount 2
        }
    }

    Context 'Argument reparsing edge cases' {
        BeforeAll {
            function Test-Reparse {
                [CmdletBinding()]
                param(
                    [string] $Name,
                    [switch] $Verbose2,
                    [bool]   $Flag,
                    [string] $Other
                )
                @{
                    Name    = $Name
                    Verbose2 = $Verbose2.IsPresent
                    Flag    = $Flag
                    Other   = $Other
                }
            }
        }

        It 'Switch parameter with no argument defaults to True' {
            $result = Test-Reparse -Verbose2
            $result.Verbose2 | Should -BeTrue
        }

        It 'Switch parameter with explicit colon-False is False' {
            $result = Test-Reparse -Verbose2:$false
            $result.Verbose2 | Should -BeFalse
        }

        It 'Bool parameter with colon-true syntax is True' {
            $result = Test-Reparse -Flag:$true
            $result.Flag | Should -BeTrue
        }

        It 'Bool parameter with no argument throws MissingArgument' {
            { Test-Reparse -Flag } | Should -Throw -ErrorId 'MissingArgument,Test-Reparse'
        }

        It 'Bool parameter with explicit colon-False is False' {
            $result = Test-Reparse -Flag:$false
            $result.Flag | Should -BeFalse
        }

        It 'Named string parameter followed by value binds correctly' {
            $result = Test-Reparse -Name 'hello'
            $result.Name | Should -BeExactly 'hello'
        }

        It 'Named string parameter with colon syntax binds correctly' {
            $result = Test-Reparse -Name:'hello'
            $result.Name | Should -BeExactly 'hello'
        }

        It 'Named string parameter with no value and no following arg throws MissingArgument' {
            { Test-Reparse -Name } | Should -Throw -ErrorId 'MissingArgument,Test-Reparse'
        }

        It 'Named string parameter followed by another named parameter throws MissingArgument' {
            { Test-Reparse -Name -Other } | Should -Throw -ErrorId 'MissingArgument,Test-Reparse'
        }

        It 'Multiple named parameters each with values bind independently' {
            $result = Test-Reparse -Name 'alpha' -Other 'beta'
            $result.Name  | Should -BeExactly 'alpha'
            $result.Other | Should -BeExactly 'beta'
        }

        It 'Named + switch interspersed all bind correctly' {
            $result = Test-Reparse -Name 'foo' -Verbose2 -Other 'bar'
            $result.Name    | Should -BeExactly 'foo'
            $result.Verbose2 | Should -BeTrue
            $result.Other   | Should -BeExactly 'bar'
        }
    }

    Context 'Type coercion edge cases during parameter binding' {
        It 'String value coerced to [int] parameter' {
            function Test-CoerceInt {
                [CmdletBinding()]
                param([int] $Value)
                $Value
            }
            $result = Test-CoerceInt -Value '42'
            $result | Should -Be 42
            $result | Should -BeOfType [int]
        }

        It 'Int value coerced to [string] parameter' {
            function Test-CoerceStr {
                [CmdletBinding()]
                param([string] $Value)
                $Value
            }
            $result = Test-CoerceStr -Value 99
            $result | Should -BeExactly '99'
            $result | Should -BeOfType [string]
        }

        It 'Single value coerced to [string[]] parameter becomes single-element array' {
            function Test-CoerceArray {
                [CmdletBinding()]
                param([string[]] $Values)
                $Values
            }
            # PowerShell unrolls single-element arrays on function return;
            # wrap in @() to verify only one element was bound.
            $result = @(Test-CoerceArray -Values 'hello')
            $result | Should -HaveCount 1
            $result[0] | Should -BeExactly 'hello'
        }

        It 'Array value bound to [string[]] parameter keeps all elements' {
            function Test-CoerceArrayMulti {
                [CmdletBinding()]
                param([string[]] $Values)
                $Values
            }
            $result = Test-CoerceArrayMulti -Values @('a', 'b', 'c')
            $result | Should -HaveCount 3
        }

        It 'String coerced to [System.Uri] parameter' {
            function Test-CoerceUri {
                [CmdletBinding()]
                param([System.Uri] $Uri)
                $Uri.AbsoluteUri
            }
            $result = Test-CoerceUri -Uri 'https://example.com'
            $result | Should -Match 'example\.com'
        }

        It 'String coerced to [System.Version] parameter' {
            function Test-CoerceVersion {
                [CmdletBinding()]
                param([System.Version] $Ver)
                $Ver.Major
            }
            $result = Test-CoerceVersion -Ver '3.14.0'
            $result | Should -Be 3
        }

        It '$null coerced to [int] parameter becomes 0 (no exception)' {
            function Test-NullInt {
                [CmdletBinding()]
                param([int] $Value = 0)
                $Value
            }
            # $null is converted to 0 for [int] parameters in PowerShell.
            $result = Test-NullInt -Value $null
            $result | Should -Be 0
        }

        It 'Unconvertible string throws ParameterArgumentTransformationError for [int]' {
            function Test-BadInt {
                [CmdletBinding()]
                param([int] $Value)
                $Value
            }
            { Test-BadInt -Value 'not-a-number' } | Should -Throw -ErrorId 'ParameterArgumentTransformationError,Test-BadInt'
        }

        It 'Enum string coerced to attribute param enum (PSCmdlet ParameterSet)' {
            function Test-CoerceEnum {
                [CmdletBinding()]
                param(
                    [Parameter()]
                    [System.IO.FileMode] $Mode
                )
                $Mode
            }
            $result = Test-CoerceEnum -Mode 'Open'
            $result | Should -Be ([System.IO.FileMode]::Open)
        }

        It 'ScriptBlock value bound to [ScriptBlock] parameter without coercion' {
            function Test-SBParam {
                [CmdletBinding()]
                param([scriptblock] $Filter)
                $Filter
            }
            $sb = { $_ -gt 0 }
            $result = Test-SBParam -Filter $sb
            $result | Should -BeOfType [scriptblock]
        }
    }

    Context 'Parameter validation attribute edge cases' {
        It '[ValidateNotNull] rejects null' {
            function Test-VNN {
                [CmdletBinding()]
                # Use [object] type to allow null — [string] converts $null to empty string
                param([ValidateNotNull()] [object] $Value)
                $Value
            }
            { Test-VNN -Value $null } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VNN'
        }

        It '[ValidateNotNullOrEmpty] rejects empty string' {
            function Test-VNNOE {
                [CmdletBinding()]
                param([ValidateNotNullOrEmpty()] [string] $Value)
                $Value
            }
            { Test-VNNOE -Value '' } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VNNOE'
        }

        It '[ValidateNotNullOrEmpty] accepts non-empty string' {
            function Test-VNNOEPass {
                [CmdletBinding()]
                param([ValidateNotNullOrEmpty()] [string] $Value)
                $Value
            }
            Test-VNNOEPass -Value 'hello' | Should -BeExactly 'hello'
        }

        It '[ValidateRange] rejects out-of-range value' {
            function Test-VRange {
                [CmdletBinding()]
                param([ValidateRange(1, 10)] [int] $Value)
                $Value
            }
            { Test-VRange -Value 0  } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VRange'
            { Test-VRange -Value 11 } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VRange'
        }

        It '[ValidateRange] accepts in-range value' {
            function Test-VRangePass {
                [CmdletBinding()]
                param([ValidateRange(1, 10)] [int] $Value)
                $Value
            }
            Test-VRangePass -Value 5 | Should -Be 5
        }

        It '[ValidateSet] rejects value not in set' {
            function Test-VSet {
                [CmdletBinding()]
                param([ValidateSet('Red', 'Green', 'Blue')] [string] $Color)
                $Color
            }
            { Test-VSet -Color 'Purple' } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VSet'
        }

        It '[ValidateSet] case-insensitive match succeeds and preserves input case' {
            function Test-VSetCase {
                [CmdletBinding()]
                param([ValidateSet('Red', 'Green', 'Blue')] [string] $Color)
                $Color
            }
            # ValidateSet matches case-insensitively but returns the original input value
            Test-VSetCase -Color 'red' | Should -BeExactly 'red'
        }

        It '[ValidatePattern] rejects non-matching string' {
            function Test-VPat {
                [CmdletBinding()]
                param([ValidatePattern('^\d+$')] [string] $Value)
                $Value
            }
            { Test-VPat -Value 'abc' } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VPat'
        }

        It '[ValidatePattern] accepts matching string' {
            function Test-VPatPass {
                [CmdletBinding()]
                param([ValidatePattern('^\d+$')] [string] $Value)
                $Value
            }
            Test-VPatPass -Value '123' | Should -BeExactly '123'
        }

        It '[ValidateLength] rejects string outside length bounds' {
            function Test-VLen {
                [CmdletBinding()]
                param([ValidateLength(2, 5)] [string] $Value)
                $Value
            }
            { Test-VLen -Value 'a'      } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VLen'
            { Test-VLen -Value 'toolong' } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VLen'
        }

        It '[ValidateScript] rejects when script returns falsy' {
            function Test-VScript {
                [CmdletBinding()]
                param([ValidateScript({ $_ -gt 0 })] [int] $Value)
                $Value
            }
            { Test-VScript -Value -1 } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VScript'
        }

        It '[ValidateScript] accepts when script returns truthy' {
            function Test-VScriptPass {
                [CmdletBinding()]
                param([ValidateScript({ $_ -gt 0 })] [int] $Value)
                $Value
            }
            Test-VScriptPass -Value 5 | Should -Be 5
        }

        It '[ValidateCount] rejects array with wrong element count' {
            function Test-VCount {
                [CmdletBinding()]
                param([ValidateCount(2, 4)] [string[]] $Values)
                $Values
            }
            { Test-VCount -Values @('a') } | Should -Throw -ErrorId 'ParameterArgumentValidationError,Test-VCount'
        }
    }
}
