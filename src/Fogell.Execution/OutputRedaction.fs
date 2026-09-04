namespace Fogell.Execution

open System
open System.Text
open System.Collections.Generic

/// Text plus exact provenance for the `****` spans produced by the raw matcher.
/// Literal stars from a process are never promoted merely because they have the
/// same bytes; callers can read [Text] but only the masking pipeline can mint
/// protected spans.
[<Sealed>]
type RedactedText internal (text: string, tokenCharacters: bool array, sourceCharacters: int array) =
    do
        if isNull text then nullArg (nameof text)
        if isNull tokenCharacters then nullArg (nameof tokenCharacters)
        if isNull sourceCharacters then nullArg (nameof sourceCharacters)
        if text.Length <> tokenCharacters.Length then invalidArg (nameof tokenCharacters) "redaction provenance length differs from text"
        if text.Length <> sourceCharacters.Length then invalidArg (nameof sourceCharacters) "source provenance length differs from text"

        tokenCharacters
        |> Array.iteri (fun index isToken ->
            if isToken && text[index] <> '*' then
                invalidArg (nameof tokenCharacters) "redaction provenance protects a non-token character")

    member _.Text = text
    member internal _.TokenCharacters = tokenCharacters
    member internal _.SourceCharacters = sourceCharacters
    override _.ToString() = text

    /// Mint ordinary raw text. This can never create protected token spans.
    static member Raw(value: string) =
        let value = if isNull value then "" else value
        RedactedText(value, Array.create value.Length false, Array.create value.Length -1)

    /// Reconstruct the raw line separators removed by callback framing while
    /// preserving every existing token bit. The appended separators are raw.
    static member JoinLines(values: seq<RedactedText>) =
        let joinedText = StringBuilder()
        let joinedTokens = ResizeArray<bool>()
        let joinedSources = ResizeArray<int>()

        for value in values do
            joinedText.Append value.Text |> ignore
            joinedText.Append '\n' |> ignore
            joinedTokens.AddRange value.TokenCharacters
            joinedSources.AddRange value.SourceCharacters
            joinedTokens.Add false
            joinedSources.Add(if value.SourceCharacters.Length = 0 then -1 else value.SourceCharacters[value.SourceCharacters.Length - 1])

        RedactedText(joinedText.ToString(), joinedTokens.ToArray(), joinedSources.ToArray())

    static member internal JoinSourcedLines(values: seq<int * RedactedText>) =
        let joinedText = StringBuilder()
        let joinedTokens = ResizeArray<bool>()
        let joinedSources = ResizeArray<int>()

        for source, value in values do
            joinedText.Append value.Text |> ignore
            joinedText.Append '\n' |> ignore
            joinedTokens.AddRange value.TokenCharacters
            joinedSources.AddRange(Array.create value.Text.Length source)
            joinedTokens.Add false
            joinedSources.Add source

        RedactedText(joinedText.ToString(), joinedTokens.ToArray(), joinedSources.ToArray())

    /// Incremental-reader line semantics over a provenance-bearing value.
    /// CR, LF, and CRLF frame lines; empty lines are retained exactly as the
    /// process callback contract retains them.
    member this.SplitLines() =
        let lines = ResizeArray<RedactedText>()
        let mutable start = 0
        let mutable index = 0

        let addLine finish =
            let tokens =
                if finish = start then Array.empty
                else this.TokenCharacters[start .. finish - 1]

            lines.Add(
                RedactedText(
                    this.Text.Substring(start, finish - start),
                    tokens,
                    (if finish = start then Array.empty else this.SourceCharacters[start .. finish - 1])))

        while index < this.Text.Length do
            match this.Text[index] with
            | '\r' ->
                addLine index
                index <- index + 1
                if index < this.Text.Length && this.Text[index] = '\n' then index <- index + 1
                start <- index
            | '\n' ->
                addLine index
                index <- index + 1
                start <- index
            | _ -> index <- index + 1

        if start < this.Text.Length then addLine this.Text.Length
        lines.ToArray()

    member internal this.SplitLinesWithSources() =
        let lines = ResizeArray<int * RedactedText>()
        let mutable start = 0
        let mutable index = 0

        let addLine finish separatorFinish =
            let value =
                RedactedText(
                    this.Text.Substring(start, finish - start),
                    (if finish = start then Array.empty else this.TokenCharacters[start .. finish - 1]),
                    (if finish = start then Array.empty else this.SourceCharacters[start .. finish - 1]))

            let sourceStart, sourceFinish =
                if finish > start then start, finish
                else finish, separatorFinish

            let source =
                if sourceFinish <= sourceStart then -1
                else this.SourceCharacters[sourceStart .. sourceFinish - 1] |> Array.max

            lines.Add(source, value)

        while index < this.Text.Length do
            match this.Text[index] with
            | '\r' ->
                let finish = index
                index <- index + 1
                if index < this.Text.Length && this.Text[index] = '\n' then index <- index + 1
                addLine finish index
                start <- index
            | '\n' ->
                let finish = index
                index <- index + 1
                addLine finish index
                start <- index
            | _ -> index <- index + 1

        if start < this.Text.Length then addLine this.Text.Length this.Text.Length
        lines.ToArray()

type internal RedactedTextBuilder() =
    let text = StringBuilder()
    let tokenCharacters = ResizeArray<bool>()
    let sourceCharacters = ResizeArray<int>()

    member _.AppendRaw(value: string) =
        if not (String.IsNullOrEmpty value) then
            text.Append value |> ignore
            for _ = 1 to value.Length do tokenCharacters.Add false
            for _ = 1 to value.Length do sourceCharacters.Add -1

    member _.AppendRawSourced(value: string, source: int) =
        if not (String.IsNullOrEmpty value) then
            text.Append value |> ignore
            for _ = 1 to value.Length do tokenCharacters.Add false
            for _ = 1 to value.Length do sourceCharacters.Add source

    member _.AppendRaw(value: char) =
        text.Append value |> ignore
        tokenCharacters.Add false
        sourceCharacters.Add -1

    member _.AppendRawSourced(value: char, source: int) =
        text.Append value |> ignore
        tokenCharacters.Add false
        sourceCharacters.Add source

    member _.AppendProtected(value: string) =
        if not (String.IsNullOrEmpty value) then
            text.Append value |> ignore
            for _ = 1 to value.Length do tokenCharacters.Add true
            for _ = 1 to value.Length do sourceCharacters.Add -1

    member _.AppendProtectedSourced(value: string, source: int) =
        if not (String.IsNullOrEmpty value) then
            text.Append value |> ignore
            for _ = 1 to value.Length do tokenCharacters.Add true
            for _ = 1 to value.Length do sourceCharacters.Add source

    member _.AppendToken() =
        text.Append "****" |> ignore
        for _ = 1 to 4 do tokenCharacters.Add true
        for _ = 1 to 4 do sourceCharacters.Add -1

    member _.AppendTokenSourced(source: int) =
        text.Append "****" |> ignore
        for _ = 1 to 4 do tokenCharacters.Add true
        for _ = 1 to 4 do sourceCharacters.Add source

    member _.Append(value: RedactedText) =
        text.Append value.Text |> ignore
        tokenCharacters.AddRange value.TokenCharacters
        sourceCharacters.AddRange value.SourceCharacters

    member this.AppendLine(value: RedactedText) =
        this.Append value
        this.AppendRaw '\n'

    member _.Clear() =
        text.Clear() |> ignore
        tokenCharacters.Clear()
        sourceCharacters.Clear()

    member _.Length = text.Length
    member _.ToRedactedText() = RedactedText(text.ToString(), tokenCharacters.ToArray(), sourceCharacters.ToArray())

module internal RedactedTextOps =
    let raw text =
        RedactedText.Raw text

    let append (first: RedactedText) (second: RedactedText) =
        let combined = RedactedTextBuilder()
        combined.Append first
        combined.Append second
        combined.ToRedactedText()

    /// Keep only characters whose framed-source index is selected, without
    /// flattening the protected-token provenance carried by those characters.
    let retainSources (selected: Set<int>) (value: RedactedText) =
        let retained = RedactedTextBuilder()

        for index = 0 to value.Text.Length - 1 do
            let source = value.SourceCharacters[index]

            if selected.Contains source then
                if value.TokenCharacters[index] then
                    retained.AppendProtectedSourced(string value.Text[index], source)
                else
                    retained.AppendRawSourced(value.Text[index], source)

        retained.ToRedactedText()

    /// Apply a transform only to characters which were not produced as a mask
    /// token. Raw `****` stays raw and can therefore match a credential learned
    /// before publication; genuine matcher tokens remain opaque boundaries.
    let mapRawFragments (transform: RedactedText -> RedactedText) (value: RedactedText) =
        let output = RedactedTextBuilder()
        let raw = RedactedTextBuilder()
        let protectedAt index = value.TokenCharacters[index]

        let flushRaw () =
            if raw.Length > 0 then
                output.Append(transform (raw.ToRedactedText()))
                raw.Clear()

        let mutable index = 0

        while index < value.Text.Length do
            if protectedAt index then
                flushRaw ()
                let start = index

                while index < value.Text.Length && protectedAt index do
                    index <- index + 1

                for protectedIndex = start to index - 1 do
                    output.AppendProtectedSourced(string value.Text[protectedIndex], value.SourceCharacters[protectedIndex])
            else
                raw.AppendRawSourced(value.Text[index], value.SourceCharacters[index])
                index <- index + 1

        flushRaw ()
        output.ToRedactedText()

type private MaskPatternState =
    { Text: string
      Failure: int array
      mutable Matched: int }

type private PendingLogicalCharacter =
    { Index: int64
      Character: char
      Origin: int
      TrailingSeparator: RedactedTextBuilder }

/// FG-236. Stateful raw-output redaction. A single CR, LF, or CRLF sequence may
/// occur between adjacent characters of a registered form. Two consecutive line
/// endings end the candidate, so pending storage is bounded by three times the
/// longest form while still covering a CRLF between every pair of characters.
type internal SeparatorTolerantMasker(maskForms: unit -> string array) =
    let buildPattern (text: string) =
        let failure = Array.zeroCreate text.Length
        let mutable matched = 0

        for index = 1 to text.Length - 1 do
            while matched > 0 && text[matched] <> text[index] do
                matched <- failure[matched - 1]

            if text[matched] = text[index] then
                matched <- matched + 1

            failure[index] <- matched

        { Text = text
          Failure = failure
          Matched = 0 }

    let initialForms = maskForms ()
    let knownForms = HashSet<string>(initialForms, StringComparer.Ordinal)
    let patterns = ResizeArray(initialForms |> Array.map buildPattern)
    let pending = Queue<PendingLogicalCharacter>()
    let matchesByStart = Dictionary<int64, int>()
    let mutable longestForm =
        if Array.isEmpty initialForms then 0 else initialForms |> Array.map String.length |> Array.max

    let mutable maximumPendingCharacters = max 1 (longestForm * 3)
    let mutable lastPending: PendingLogicalCharacter option = None
    let mutable logicalIndex = -1L
    let mutable separatorCount = 0
    let mutable pendingCr = false
    let mutable pendingCrOrigin = -1
    let mutable pendingRawCharacters = 0

    let recordMatchAt endIndex length =
        let start = endIndex - int64 length + 1L

        match matchesByStart.TryGetValue start with
        | true, previous when previous >= length -> ()
        | _ -> matchesByStart[start] <- length

    let advancePatternAt (pattern: MaskPatternState) endIndex c =
        while pattern.Matched > 0 && pattern.Text[pattern.Matched] <> c do
            pattern.Matched <- pattern.Failure[pattern.Matched - 1]

        if pattern.Text[pattern.Matched] = c then
            pattern.Matched <- pattern.Matched + 1

        if pattern.Matched = pattern.Text.Length then
            recordMatchAt endIndex pattern.Text.Length
            pattern.Matched <- pattern.Failure[pattern.Matched - 1]

    let refreshPatterns () =
        for text in maskForms () do
            if knownForms.Add text then
                let pattern = buildPattern text

                // Registration is monotonic. Seed a newly registered form from
                // only the still-unpublished suffix; bytes already committed
                // before registration stay outside its retrospective reach.
                for item in pending do
                    advancePatternAt pattern item.Index item.Character

                patterns.Add pattern
                longestForm <- max longestForm text.Length

        maximumPendingCharacters <- max 1 (longestForm * 3)

    let resetPatterns () =
        for pattern in patterns do
            pattern.Matched <- 0

    let clampPatternStates () =
        let remaining = pending.Count

        for pattern in patterns do
            while pattern.Matched > remaining do
                pattern.Matched <- pattern.Failure[pattern.Matched - 1]

    let removeMatchStart index = matchesByStart.Remove index |> ignore

    let finalizeFront (output: RedactedTextBuilder) =
        let start = pending.Peek().Index

        match matchesByStart.TryGetValue start with
        | true, length ->
            let mutable tokenOrigin = -1
            let mutable trailingSeparator = RedactedText.Raw ""

            for _ = 1 to length do
                let item = pending.Dequeue()
                pendingRawCharacters <- pendingRawCharacters - 1 - item.TrailingSeparator.Length
                tokenOrigin <- max tokenOrigin item.Origin
                trailingSeparator <- item.TrailingSeparator.ToRedactedText()
                removeMatchStart item.Index

            // A separator after the last matched character is outside the
            // credential. Separators between matched characters disappear.
            output.AppendTokenSourced tokenOrigin
            output.Append trailingSeparator
        | _ ->
            let item = pending.Dequeue()
            pendingRawCharacters <- pendingRawCharacters - 1 - item.TrailingSeparator.Length
            removeMatchStart item.Index
            output.AppendRawSourced(item.Character, item.Origin)
            output.Append(item.TrailingSeparator.ToRedactedText())

        if pending.Count = 0 then
            lastPending <- None

        // KMP state is a suffix length. Once output commits a prefix, follow
        // failure links until every retained state lies wholly in the remaining
        // window; no buffered character is replayed.
        clampPatternStates ()

    let finalizeAll output =
        while pending.Count > 0 do
            finalizeFront output

    let frontHasUnfinishedCandidate () =
        let start = pending.Peek().Index

        patterns
        |> Seq.exists (fun pattern ->
            pattern.Matched > 0
            && logicalIndex - int64 pattern.Matched + 1L = start)

    let finalizeReady output =
        // Do not wait for the globally longest form when the front character
        // has already fallen out of every active pattern prefix. This preserves
        // progressive line delivery while retaining a genuinely ambiguous
        // prefix until it matches, fails, hits a grammar barrier, or reaches EOF.
        while pending.Count > 0 && not (frontHasUnfinishedCandidate ()) do
            finalizeFront output

    let assertBound () =
        let count = pendingRawCharacters + if pendingCr then 1 else 0

        if count > maximumPendingCharacters then
            invalidOp "raw-output redaction exceeded its grammar-derived pending bound"

    let addSeparator (output: RedactedTextBuilder) (text: string) origin =
        separatorCount <- separatorCount + 1

        match lastPending with
        | Some item ->
            item.TrailingSeparator.AppendRawSourced(text, origin)
            pendingRawCharacters <- pendingRawCharacters + text.Length
        | None -> output.AppendRawSourced(text, origin)

        if separatorCount >= 2 then
            // A second physical ending is a hard grammar barrier. Everything
            // before it can be resolved now; no pattern state crosses it.
            finalizeAll output
            resetPatterns ()

        assertBound ()

    let addLogical (output: RedactedTextBuilder) (c: char) origin =
        if separatorCount >= 2 then
            resetPatterns ()

        separatorCount <- 0
        logicalIndex <- logicalIndex + 1L

        let item =
            { Index = logicalIndex
              Character = c
              Origin = origin
              TrailingSeparator = RedactedTextBuilder() }

        pending.Enqueue item
        lastPending <- Some item
        pendingRawCharacters <- pendingRawCharacters + 1

        for pattern in patterns do
            advancePatternAt pattern logicalIndex c

        finalizeReady output
        assertBound ()

    let rec addRawCharacter (output: RedactedTextBuilder) (c: char) origin =
        if pendingCr then
            pendingCr <- false

            if c = '\n' then
                addSeparator output "\r\n" (max pendingCrOrigin origin)
            else
                addSeparator output "\r" pendingCrOrigin
                addRawCharacter output c origin
        else
            match c with
            | '\r' ->
                pendingCr <- true
                pendingCrOrigin <- origin
                assertBound ()
            | '\n' -> addSeparator output "\n" origin
            | _ -> addLogical output c origin

    let pushValue (output: RedactedTextBuilder) (value: RedactedText) =
        for index = 0 to value.Text.Length - 1 do
            addRawCharacter output value.Text[index] value.SourceCharacters[index]

    member _.PushValue(value: RedactedText) =
        refreshPatterns ()

        if patterns.Count = 0 || String.IsNullOrEmpty value.Text then
            value
        else
            let output = RedactedTextBuilder()
            finalizeReady output
            pushValue output value
            output.ToRedactedText()

    member this.PushRedacted(text: string) = this.PushValue(RedactedText.Raw text)

    member this.Push(text: string) = (this.PushRedacted text).Text

    member _.CompleteRedacted() =
        refreshPatterns ()

        if patterns.Count = 0 then
            RedactedTextOps.raw ""
        else
            let output = RedactedTextBuilder()

            if pendingCr then
                pendingCr <- false
                addSeparator output "\r" pendingCrOrigin

            finalizeAll output
            resetPatterns ()

            output.ToRedactedText()

    member this.Complete() = (this.CompleteRedacted()).Text

    member _.PendingCharacters = pendingRawCharacters + if pendingCr then 1 else 0
    member _.MaximumPendingCharacters = maximumPendingCharacters

/// Opaque policy carried across the executor/process boundary. Callers cannot
/// mutate matcher state or accidentally share it between stdout and stderr.
type OutputRedactionPolicy internal (maskForms: unit -> string list, synchronizationRoot: obj option) =
    let synchronizationRoot = defaultArg synchronizationRoot (obj ())

    let currentForms () =
        let forms =
            maskForms ()
            |> List.filter (String.IsNullOrEmpty >> not)
            |> List.distinct
            |> List.sortByDescending String.length
            |> List.toArray

        if forms |> Array.exists (fun form -> form.IndexOfAny([| '\r'; '\n' |]) >= 0) then
            invalidArg (nameof maskForms) "registered raw-output mask forms must be single-line"

        forms

    new(maskForms: string list) = OutputRedactionPolicy((fun () -> maskForms), None)

    member internal _.Synchronize(action: unit -> 'T) =
        lock synchronizationRoot action

    member internal this.CreateMatcher() =
        this.Synchronize(fun () -> SeparatorTolerantMasker currentForms)

    member internal this.MaskRedacted(text: string) =
        this.Synchronize(fun () ->
            let matcher = SeparatorTolerantMasker currentForms
            RedactedTextOps.append (matcher.PushRedacted text) (matcher.CompleteRedacted()))

    member internal this.Mask(text: string) = (this.MaskRedacted text).Text

    /// Publish only bytes proven safe without claiming the supplied snapshot is
    /// a true EOF. Any ambiguous suffix remains deliberately withheld.
    member internal this.MaskAvailablePrefixRedacted(text: string) =
        this.Synchronize(fun () ->
            let matcher = SeparatorTolerantMasker currentForms
            matcher.PushRedacted text)

    member internal this.MaskAvailablePrefix(text: string) =
        (this.MaskAvailablePrefixRedacted text).Text

    /// Recheck a buffer while preserving only spans which the matcher actually
    /// produced. Literal `****` is raw output and remains eligible to match a
    /// credential learned before publication.
    member internal this.MaskAlreadyRedacted(value: RedactedText) =
        this.Synchronize(fun () ->
            let forms = currentForms ()

            let apply (raw: RedactedText) =
                if Array.isEmpty forms then
                    raw
                else
                    let matcher = SeparatorTolerantMasker(fun () -> forms)
                    RedactedTextOps.append (matcher.PushValue raw) (matcher.CompleteRedacted())

            RedactedTextOps.mapRawFragments apply value)

    member internal this.IsEmpty =
        this.Synchronize(fun () -> Array.isEmpty (currentForms ()))
