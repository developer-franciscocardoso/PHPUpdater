function LoadHelperError(ReportName, Fallback: String): String;
var
  Content: AnsiString;
begin
  Result := Fallback;
  if LoadStringFromFile(ReportName + '.error', Content) then
    Result := Utf8Decode(Content);
end;

procedure ExtractStartupFiles();
begin
  ExtractTemporaryFiles('{tmp}\engine\*');
  ExtractTemporaryFiles('{tmp}\payload\manifest.json');
  if CompareText(GetSHA256OfFile(
      ExpandConstant('{tmp}\payload\manifest.json')),
      '{#PayloadManifestHash}') <> 0 then
    RaiseException('Bundled release manifest checksum mismatch.');
end;
