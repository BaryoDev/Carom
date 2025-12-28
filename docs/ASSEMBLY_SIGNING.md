# Assembly Signing for Carom

## Current Status

Assembly signing is **optional** for Carom packages. We use **PublicSign** for open-source compatibility.

## Why PublicSign?

- **Cross-platform**: Works on Windows, macOS, and Linux
- **Open Source Friendly**: No need for private keys
- **NuGet Compatible**: Fully supported by NuGet.org
- **Tamper Detection**: Provides assembly identity verification

## Strong-Name Signing (Optional)

For enterprise scenarios requiring traditional strong-name signing:

### On Windows

```bash
sn -k carom.snk
```

### On macOS/Linux

Strong-name signing requires Windows or Mono. For cross-platform projects, we recommend:

1. **PublicSign** (current approach) - Works everywhere
2. **Authenticode** - Sign with certificate after build
3. **NuGet Package Signing** - Sign the `.nupkg` file

## Current Configuration

All `.csproj` files are configured with:

```xml
<PropertyGroup>
  <SignAssembly>false</SignAssembly>
  <PublicSign>true</PublicSign>
</PropertyGroup>
```

This provides:
- ✅ Assembly identity
- ✅ Tamper detection
- ✅ Cross-platform compatibility
- ✅ No private key management

## For Production

Consider adding:

1. **NuGet Package Signing**: Sign `.nupkg` files with certificate
2. **Authenticode**: Sign assemblies with code signing certificate
3. **Azure Key Vault**: Store signing certificates securely

## References

- [.NET Assembly Signing](https://docs.microsoft.com/en-us/dotnet/standard/assembly/sign-strong-name)
- [PublicSign Documentation](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Public%20Signing.md)
- [NuGet Package Signing](https://docs.microsoft.com/en-us/nuget/create-packages/sign-a-package)
