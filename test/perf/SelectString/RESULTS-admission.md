# Matching-work admission: initial standalone experiment

Measured 2026-10-23. This changes only the benchmark prototype, tests and probe.
Production sources and existing isolated cmdlet binaries are unchanged. The new
gate has not been integrated into or measured through Select-String itself.

## Gate and checks

MatchingWorkGate counts wall time around foreground TryMatch calls, excluding
reading/output and gaps between records. Clock samples still include preemption,
GC and clock-read overhead. A decision is made on the next input after the
threshold is reached, never immediately after the last input. Admission attempts
are bounded/nonblocking, with no retry after rejection. Timing stops after the
decision or stop notification. Construction culture, atomic publication, fault
observation and joined cleanup are retained.

The gate times every call or calls numbered 0,16,32,... with an exploratory 0.5 ms
observed-time threshold. It does not extrapolate samples. Periodic sampling can
miss isolated expensive calls or correlate with input periodicity. Warm variants
use a known fixture input, not arbitrary production input.

64 engine/prototype tests pass. New deterministic clock-based checks cover exact
thresholds, exclusion of between-call time, final-input behavior, stop suppression,
busy rejection without retry and sampled accounting. Culture/capture and worker
fault tests now also exercise work-based admission. The benchmark project builds
without warnings/errors.

## Method

The admission probe covers Literal/SqlDuration, Match/LongNearMiss, and 1/4096
records. Eleven modes include direct interpretation, wrapper-only interpretation,
every-call/sampled timing without admission, the prior six compilation controls,
and two work gates. Each run rotates mode order with three warmups and nine
measured calls. Two runs per affinity (1 and 5) reverse affinity order: one logical
CPU versus two previously verified physical cores. There are 352 cases / 3,168
measured calls; all counts validate. All single-input work-gate calls avoid
admission. Compilation faults fail the probe; each invocation joins before the
next begins.

Total wall time includes engine/gate construction, matching and disposal. CPU and
managed allocation are retained in raw data; short CPU observations are coarse.
The probe loop itself checks first-match/publication state. These are standalone
measurements, not end-to-end cmdlet timings or BenchmarkDotNet confidence intervals.
Ranges below are paired median total milliseconds. Historical cmdlet numbers must
not be compared directly with these rows.

## Results

### Literal / Match, 4096 records

| Mode | One CPU ms | Two cores ms |
| --- | ---: | ---: |
| Interpreted | 0.3548-0.3561 | 0.3572-0.3585 |
| WrapperOnly | 0.3778-0.3810 | 0.3744-0.3818 |
| TimingEveryCall | 0.4862-0.5147 | 0.4897-0.5160 |
| TimingSampled | 0.3879-0.3928 | 0.3859-0.3999 |
| Compiled | 0.6584-0.6680 | 0.6918-0.6963 |
| DelayedSynchronous | 0.6552-0.6558 | 0.6645-0.6700 |
| BackgroundWarm | 0.4197-0.4229 | 0.4389-0.4477 |
| BackgroundDelayedWarm | 0.4091-0.4263 | 0.4254-0.4346 |
| WorkEveryCall | 0.4859-0.5119 | 0.4905-0.5149 |
| WorkSampled | 0.3816-0.3949 | 0.3846-0.4058 |

### Literal / LongNearMiss, 4096 records

| Mode | One CPU ms | Two cores ms |
| --- | ---: | ---: |
| Interpreted | 0.6593-0.6594 | 0.6564-0.6644 |
| WrapperOnly | 0.6698-0.6816 | 0.6722-0.6816 |
| TimingEveryCall | 0.7828-0.8110 | 0.7827-0.8096 |
| TimingSampled | 0.6817-0.6968 | 0.6799-0.6994 |
| Compiled | 1.0014-1.0219 | 1.0155-1.0500 |
| DelayedSynchronous | 0.9859-0.9985 | 0.9831-0.9940 |
| BackgroundWarm | 0.7178-0.7236 | 0.6835-0.7088 |
| BackgroundDelayedWarm | 0.7097-0.7230 | 0.6830-0.6967 |
| WorkEveryCall | 0.7970-0.8186 | 0.9685-1.0021 |
| WorkSampled | 0.6828-0.6978 | 0.6898-0.7159 |

### SqlDuration / Match, 4096 records

| Mode | One CPU ms | Two cores ms |
| --- | ---: | ---: |
| Interpreted | 0.6228-0.6230 | 0.6221-0.6848 |
| WrapperOnly | 0.6565-0.6886 | 0.6400-0.7836 |
| TimingEveryCall | 0.7556-0.7843 | 0.7873-0.8027 |
| TimingSampled | 0.6467-0.6715 | 0.6821-0.8065 |
| Compiled | 2.1145-2.3221 | 2.1988-2.2119 |
| DelayedSynchronous | 2.1584-2.4085 | 2.1719-2.2035 |
| BackgroundWarm | 0.7017-0.7348 | 1.9348-1.9532 |
| BackgroundDelayedWarm | 0.6715-0.7533 | 1.8681-1.8852 |
| WorkEveryCall | 0.7783-0.8246 | 2.4621-2.4655 |
| WorkSampled | 0.6475-0.6991 | 0.6687-0.6910 |

### SqlDuration / LongNearMiss, 4096 records

| Mode | One CPU ms | Two cores ms |
| --- | ---: | ---: |
| Interpreted | 469.5649-497.6119 | 456.2523-460.1935 |
| WrapperOnly | 468.7444-496.5038 | 457.8857-458.5381 |
| TimingEveryCall | 466.2365-497.7789 | 456.9661-462.3794 |
| TimingSampled | 472.1922-498.7120 | 457.9273-464.6989 |
| Compiled | 113.4457-114.5396 | 111.5102-112.4350 |
| DelayedSynchronous | 124.2742-125.2600 | 122.6317-124.0949 |
| BackgroundWarm | 121.2692-133.8482 | 110.5681-112.2798 |
| BackgroundDelayedWarm | 133.3154-133.7235 | 124.3395-124.5319 |
| WorkEveryCall | 121.2764-132.5269 | 110.4069-112.0676 |
| WorkSampled | 130.1118-133.7455 | 117.6100-118.0554 |

## Interpretation

Every-call instrumentation is expensive for cheap matching: literal hits take
0.49-0.52 ms for 4096 records versus 0.35-0.36 ms direct or 0.37-0.38 ms wrapped.
Sampling reduces that overhead: timing-only is about 0.38-0.40 ms. Absolute cost
is small, but the relative regression makes every-call timing unattractive.

Observed cumulative time alone also admits work too late. Every-call gating admits
all 36 SQL-hit calls across both affinities, but none promote. On two cores all
18 start compilation and joined total time rises to 2.46 ms versus 0.62-0.68 ms
direct interpretation. On one CPU none start before disposal suppresses them.
Literal long-near-miss calls also all admit under this gate; only one of 36 promotes.
Their extra matching time did not establish that compilation would pay for itself.

Sampling avoids admission for all literal and SQL-hit calls in this matrix. It
still admits every expensive SQL near-miss call: the every-call gate attempts
admission after 4-5 records, and the sampled gate after 49-65 records. Every one
of these 72 work-gate calls starts and promotes. Two-core SQL-near-miss totals are
about 110-112 ms every-call and 118 ms sampled versus 456-460 ms interpreted.
One-CPU sampled totals are 130-134 ms versus 470-498 ms interpreted. Sampling's
later admission has a measurable cost; neither setting is a chosen policy.

The next step is to challenge sampling with periodic and phase-changing input,
late expensive records, and varying input length/pattern sets, while measuring
overhead and unused work. Compare cadence/threshold choices; do not tune only to
this constant-input corpus. Keep the every-call variant as a diagnostic control.
Do not integrate a production gate yet. Only a promising gate should move into a
separate isolated cmdlet build, followed by compatibility and end-to-end checks.
Joined cleanup remains accounting, not a final lifecycle policy; bounded abandoned
work is a separate unresolved experiment.

## Reproduction

See the optional `admission` profile in [README.md](README.md). Raw JSON includes
observed matching time, timing sample counts, attempted-admission record and
promotion record, as well as wall/CPU/allocation data. Source and binary hashes
identify this probe; existing regex-background/pwsh.exe still uses its older helper.

Unversioned artifacts:

- `tmp/SelectString/Run-Admission.ps1` and `Summarize-Admission.ps1`
- `tmp/SelectString/admission-forward-1/`, `admission-forward-5/`,
  `admission-reverse-5/` and `admission-reverse-1/`
- `tmp/SelectString/admission-summary.json` and `admission-source.json`
