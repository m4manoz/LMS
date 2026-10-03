<#
.SYNOPSIS
  Adds demo course categories and courses (in every authoring state) to a running local API.

.DESCRIPTION
  Works through the public API as the organization's administrator, so every rule is respected.
  Safe to run more than once: categories and courses that already exist are left alone.
  For local development only. Do not point it at a production system.

.EXAMPLE
  .\scripts\seed-demo-data.ps1
  .\scripts\seed-demo-data.ps1 -TenantSlug acme -AdminEmail admin@acme.test -AdminPassword 'Admin-pass-123'
#>
param(
  [string]$ApiUrl = 'http://localhost:5106',
  [string]$TenantSlug = 'acme',
  [string]$TenantName = 'Acme Academy',
  [string]$AdminEmail = 'admin@acme.test',
  [string]$AdminPassword = 'Admin-pass-123',
  [string]$PlatformKey = 'local-development-only-change-me'
)

$ErrorActionPreference = 'Stop'

function Invoke-Api {
  param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{}, [switch]$AllowConflict)
  $args2 = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $Headers; ContentType = 'application/json' }
  if ($null -ne $Body) { $args2.Body = ($Body | ConvertTo-Json -Depth 8) }
  try { return Invoke-RestMethod @args2 }
  catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($AllowConflict -and $status -eq 409) { return $null }
    throw "$Method $Path failed ($status): $($_.ErrorDetails.Message)"
  }
}

# ---------- organization and administrator ----------
$platform = @{ 'X-Platform-Key' = $PlatformKey }
Invoke-Api POST '/api/v1/platform/tenants' @{ name = $TenantName; slug = $TenantSlug } $platform -AllowConflict | Out-Null
Invoke-Api POST "/api/v1/platform/tenants/$TenantSlug/bootstrap-admin" @{ email = $AdminEmail; displayName = 'Admin'; password = $AdminPassword } $platform -AllowConflict | Out-Null
$session = Invoke-Api POST '/api/v1/auth/login' @{ tenantSlug = $TenantSlug; email = $AdminEmail; password = $AdminPassword }
$h = @{ 'X-Tenant-Slug' = $TenantSlug; Authorization = "Bearer $($session.accessToken)" }
Write-Host "Signed in to '$TenantSlug' as $AdminEmail"

# ---------- categories ----------
$categoryNames = 'Mathematics', 'Science', 'Languages', 'Technology', 'Arts', 'Professional skills'
foreach ($name in $categoryNames) { Invoke-Api POST '/api/v1/tenant/catalog/categories' @{ name = $name } $h -AllowConflict | Out-Null }
$categories = @{}
foreach ($c in (Invoke-Api GET '/api/v1/tenant/catalog/categories' -Headers $h)) { $categories[$c.name] = $c.id }
Write-Host "Categories: $($categories.Keys -join ', ')"

# ---------- courses ----------
# Status is where the course ends up: Draft, InReview, Published, or PublishedWithNewVersion.
$courses = @(
  @{ Code = 'MATH-101'; Title = 'Algebra Foundations'; Category = 'Mathematics'; Status = 'Published'; Capacity = 40
     Description = 'Equations, expressions and functions from the ground up.'
     Modules = @(
       @{ Title = 'Numbers and expressions'; Lessons = 'Order of operations', 'Simplifying expressions', 'Working with fractions' },
       @{ Title = 'Solving equations'; Lessons = 'One-step equations', 'Two-step equations' },
       @{ Title = 'Functions'; Lessons = 'What is a function?', 'Graphing lines' }) },
  @{ Code = 'MATH-201'; Title = 'Calculus I'; Category = 'Mathematics'; Status = 'PublishedWithNewVersion'; Capacity = 30
     Description = 'Limits, derivatives and an introduction to integrals. A new version with extra practice is being written.'
     Modules = @(
       @{ Title = 'Limits'; Lessons = 'Intuition for limits', 'Limit laws' },
       @{ Title = 'Derivatives'; Lessons = 'The derivative as a rate', 'Rules of differentiation', 'Chain rule' }) },
  @{ Code = 'SCI-101'; Title = 'Introduction to Biology'; Category = 'Science'; Status = 'Published'; Capacity = 30
     Description = 'Cells, genetics and ecosystems.'
     Modules = @(
       @{ Title = 'The cell'; Lessons = 'Cell structure', 'Membranes and transport' },
       @{ Title = 'Genetics'; Lessons = 'DNA and genes', 'Inheritance' }) },
  @{ Code = 'SCI-210'; Title = 'Chemistry Basics'; Category = 'Science'; Status = 'InReview'
     Description = 'Atoms, bonds and reactions. Waiting for review.'
     Modules = @(
       @{ Title = 'Atoms and the periodic table'; Lessons = 'Atomic structure', 'Reading the table' },
       @{ Title = 'Reactions'; Lessons = 'Balancing equations' }) },
  @{ Code = 'ENG-101'; Title = 'Academic Writing'; Category = 'Languages'; Status = 'Draft'
     Description = 'Structure, argument and citation. Ready to submit for review.'
     Modules = @(
       @{ Title = 'Planning'; Lessons = 'Finding a thesis', 'Outlining' },
       @{ Title = 'Drafting'; Lessons = 'Paragraphs that work', 'Citing sources' }) },
  @{ Code = 'TECH-101'; Title = 'Web Development Basics'; Category = 'Technology'; Status = 'Draft'; Code1 = $true
     Description = 'HTML, CSS and a first look at JavaScript.'
     Modules = @(
       @{ Title = 'HTML'; Lessons = 'Document structure', 'Links and images' },
       @{ Title = 'CSS'; Lessons = 'Selectors', 'Layout with flexbox' }) },
  @{ Code = 'TECH-220'; Title = 'Data Analysis with Spreadsheets'; Category = 'Technology'; Status = 'Draft'
     Description = ''
     Modules = @(
       @{ Title = 'Getting started'; Lessons = 'Cells, rows and columns' },
       @{ Title = 'Formulas (lessons still to come)'; Lessons = @() }) },
  @{ Code = 'ART-110'; Title = 'Drawing Fundamentals'; Category = 'Arts'; Status = 'Draft'
     Description = 'An outline has not been started yet.'; Modules = @() },
  @{ Code = 'BUS-150'; Title = 'Project Management Essentials'; Category = 'Professional skills'; Status = 'Published'
     Description = 'Planning, scheduling and delivering projects.'
     Modules = @(
       @{ Title = 'Planning'; Lessons = 'Defining scope', 'Estimating work' },
       @{ Title = 'Delivery'; Lessons = 'Tracking progress', 'Handling change' }) }
)

$existing = @{}
foreach ($c in (Invoke-Api GET '/api/v1/tenant/courses' -Headers $h)) { $existing[$c.code] = $c.id }

foreach ($spec in $courses) {
  if ($existing.ContainsKey($spec.Code)) { Write-Host "  $($spec.Code) already exists, skipped"; continue }
  $body = @{ code = $spec.Code; title = $spec.Title; description = $spec.Description; categoryId = $categories[$spec.Category] }
  if ($spec.Capacity) { $body.capacity = $spec.Capacity }
  $created = Invoke-Api POST '/api/v1/tenant/courses' $body $h
  $id = $created.course.id
  $base = "/api/v1/tenant/courses/$id"

  foreach ($m in $spec.Modules) {
    $module = Invoke-Api POST "$base/modules" @{ title = $m.Title } $h
    foreach ($lessonTitle in $m.Lessons) {
      $lesson = Invoke-Api POST "$base/modules/$($module.id)/lessons" @{ title = $lessonTitle; summary = "About $($lessonTitle.ToLower())." } $h
      Invoke-Api POST "$base/lessons/$($lesson.id)/blocks" @{ type = 'Text'; title = $lessonTitle; text = "This lesson covers $($lessonTitle.ToLower()). Replace this with the real material." } $h | Out-Null
      if ($spec.Code1 -and $lessonTitle -eq 'Selectors') {
        Invoke-Api POST "$base/lessons/$($lesson.id)/blocks" @{ type = 'Code'; title = 'Example'; language = 'css'; text = "h1 { color: teal; }`n.card { padding: 1rem; }" } $h | Out-Null
      }
    }
  }

  if ($spec.Status -in 'InReview', 'Published', 'PublishedWithNewVersion') { Invoke-Api POST "$base/submit-review" -Headers $h | Out-Null }
  if ($spec.Status -in 'Published', 'PublishedWithNewVersion') { Invoke-Api POST "$base/publish" -Headers $h | Out-Null }
  if ($spec.Status -eq 'PublishedWithNewVersion') {
    $draft = Invoke-Api POST "$base/versions" @{ changeSummary = 'Added practice problems to each derivative lesson' } $h
    $moduleId = $draft.modules[1].id
    Invoke-Api POST "$base/modules/$moduleId/lessons" @{ title = 'Practice problems'; summary = 'Extra practice.' } $h | Out-Null
  }
  Write-Host "  $($spec.Code) $($spec.Title) -> $($spec.Status)"
}

Write-Host ''
Write-Host 'Done. Open Course authoring to see the courses, and Course categories for the counts.'
