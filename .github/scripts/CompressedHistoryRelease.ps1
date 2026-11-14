function Test-CompressedHistoryRelease([int]$ReleaseNumber, $Previous) {
    return $ReleaseNumber -eq 498 -and $Previous.releaseNumber -eq 512 -and
        $Previous.commitSha -eq 'e2fd8203f42e2906c0792bde65e14631e34dabf0' -and
        $Previous.sha256 -eq '5a9a64c04142b1010764e78eb392d32393076506707b1f0e05a1bd51a2be5f1b'
}
