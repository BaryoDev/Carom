# Security Policy

## Supported Versions

We actively support the following versions with security updates:

| Version | Supported          |
| ------- | ------------------ |
| 1.4.x   | :white_check_mark: |
| 1.3.x   | :white_check_mark: |
| 1.2.x   | :x:                |
| 1.1.x   | :x:                |
| < 1.0   | :x:                |

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a security issue in Carom, please report it responsibly.

### How to Report

**DO NOT** create a public GitHub issue for security vulnerabilities.

Instead, please email security reports to:

**Email**: security@baryo.dev (if configured) or create a private security advisory on GitHub

### What to Include

Please include the following information in your report:

- **Description**: Clear description of the vulnerability
- **Impact**: What an attacker could achieve
- **Reproduction**: Step-by-step instructions to reproduce
- **Affected Versions**: Which versions are affected
- **Suggested Fix**: If you have one (optional)

### Response Timeline

- **Initial Response**: Within 48 hours
- **Status Update**: Within 7 days
- **Fix Timeline**: Depends on severity
  - **Critical**: 1-3 days
  - **High**: 1-2 weeks
  - **Medium**: 2-4 weeks
  - **Low**: Next release cycle

### Disclosure Policy

- We will acknowledge your report within 48 hours
- We will provide regular updates on our progress
- We will credit you in the security advisory (unless you prefer to remain anonymous)
- We will coordinate disclosure timing with you
- We will publish a security advisory after the fix is released

## Security Best Practices

### For Library Users

1. **Keep Updated**: Always use the latest version
2. **Validate Inputs**: Don't pass untrusted data to retry logic
3. **Monitor Logs**: Watch for unusual retry patterns
4. **Rate Limiting**: Use `Throttle` to prevent abuse
5. **Circuit Breakers**: Use `Cushion` to prevent cascade failures

### For Contributors

1. **Input Validation**: Validate all user inputs
2. **No Secrets**: Never commit API keys, passwords, or secrets
3. **Exception Handling**: Don't expose sensitive data in exceptions
4. **Dependencies**: Only add dependencies from trusted sources
5. **Code Review**: All PRs require security review

## Known Security Considerations

### Thread Safety

All Carom patterns use lock-free implementations with `Interlocked` operations. While this provides excellent performance, be aware:

- **Race Conditions**: Possible in high-concurrency scenarios
- **Mitigation**: Patterns are designed to be safe under race conditions

### Denial of Service

Retry logic can amplify load on failing services:

- **Mitigation**: Use `Cushion` (Circuit Breaker) to prevent retry storms
- **Mitigation**: Use `Throttle` (Rate Limiting) to control request rates
- **Mitigation**: Set reasonable retry limits (default: 3)

### Resource Exhaustion

Bulkhead pattern uses `SemaphoreSlim`:

- **Mitigation**: Set appropriate `MaxConcurrency` limits
- **Mitigation**: Monitor semaphore wait times
- **Mitigation**: Use timeouts to prevent indefinite blocking

## Security Updates

Security updates will be published as:

1. **GitHub Security Advisory**
2. **NuGet Package Update**
3. **CHANGELOG.md Entry**
4. **GitHub Release Notes**

Subscribe to GitHub notifications to receive security alerts.

## Contact

- **Security Issues**: security@baryo.dev (if configured)
- **General Issues**: https://github.com/BaryoDev/Carom/issues
- **Discussions**: https://github.com/BaryoDev/Carom/discussions

## Acknowledgments

We thank the following security researchers for responsible disclosure:

_(None yet - be the first!)_

---

**Last Updated**: 2025-12-28
**Policy Version**: 1.0
