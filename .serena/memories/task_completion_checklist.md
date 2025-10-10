# Task Completion Checklist: octo-bot-services

When you complete a coding task, follow this checklist to ensure quality and consistency.

## 1. Build Verification

```bash
# Always build with DebugL configuration for local development
dotnet build Octo.Bots.sln --configuration DebugL
```

**Success Criteria:**
- Build completes without errors
- Build completes without warnings (warnings are treated as errors)
- All projects compile successfully

## 2. Run Tests

```bash
# Run all unit tests (fast, no external dependencies)
dotnet test --filter "Category!=Integration" --configuration DebugL

# If MongoDB tools are installed, run integration tests
dotnet test --filter "Category=Integration" --configuration DebugL
```

**Success Criteria:**
- All tests pass
- No test failures
- No test skips (unless intentional)

## 3. Code Style Compliance

**Automatic Checks:**
- **Nullable Reference Types**: All warnings about nullability are treated as errors
- **Warnings as Errors**: Any warnings will fail the build
- **ImplicitUsings**: Ensure you're not adding redundant `using` statements

**Manual Review:**
- Test naming follows pattern: `MethodName_StateUnderTest_ExpectedBehavior`
- XML documentation added for public APIs
- Complex logic has inline comments

## 4. Test Coverage (Optional but Recommended)

```bash
# Generate code coverage report
dotnet test --collect:"XPlat Code Coverage" --configuration DebugL
```

**Review:**
- New code has reasonable test coverage
- Critical paths are tested
- Edge cases are considered

## 5. TypeScript Type Checking (if applicable)

If you modified TypeScript files in `src/RepositoryUpdate`:

```bash
cd src/RepositoryUpdate
npm run type-check
```

**Success Criteria:**
- No TypeScript compilation errors
- Type definitions are correct

## 6. Configuration Validation

**If you modified configuration:**
- Verify `appsettings.json` is valid JSON
- Check that required settings are present
- Ensure sensitive data is not committed

## 7. Dependency Verification

**If you added/updated packages:**

```bash
# Restore packages
dotnet restore Octo.Bots.sln

# Verify package references
dotnet list package
```

**Check:**
- OctoMesh packages use `$(OctoVersion)` variable
- No unnecessary dependencies added
- Versions are appropriate

## 8. Interface Compliance (for Jobs)

**If you added/modified a background job:**
- Interface exists: `I{JobName}Job`
- Implementation exists: `{JobName}Job`
- Job is registered in `src/BotServices/Program.cs`
- Hangfire job configuration is correct

## 9. Documentation

**Update if necessary:**
- XML documentation comments for new public APIs
- CLAUDE.md (if architecture changes)
- README.md (if user-facing features change)

## 10. Git Hygiene

```bash
# Check what will be committed
git status
git diff

# Ensure no unintended files are staged
```

**Before committing:**
- No build output files (`bin/`, `obj/`)
- No IDE-specific files (unless intentional)
- No secrets or sensitive data
- Commit message follows pattern: `AB#<work-item>: <description>`

## 11. Integration Verification (if applicable)

**If your changes affect:**
- Background jobs: Test via Hangfire Dashboard
- API endpoints: Test via Swagger UI
- MongoDB operations: Verify with actual MongoDB instance
- Authentication: Test with valid JWT tokens

## Summary Checklist

- [ ] Build succeeds with DebugL configuration
- [ ] All unit tests pass
- [ ] Integration tests pass (if applicable and MongoDB tools available)
- [ ] No build warnings
- [ ] Code follows naming conventions
- [ ] Tests follow naming pattern
- [ ] TypeScript type checks (if applicable)
- [ ] Configuration is valid
- [ ] Dependencies are correct
- [ ] Jobs are properly registered (if applicable)
- [ ] Documentation updated
- [ ] Git status is clean
- [ ] Commit message is descriptive

## Quick Command Sequence

For most tasks, run this sequence:

```bash
# Build
dotnet build Octo.Bots.sln --configuration DebugL

# Test
dotnet test --filter "Category!=Integration" --configuration DebugL

# Check status
git status
```

If all green ✅, you're ready to commit!
