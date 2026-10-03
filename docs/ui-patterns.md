# UI patterns

Every screen follows the pattern first used by Course authoring. The building blocks live in
`src/Lms.Web/src/components/form.tsx` (plus `components/SidePanel.tsx` and the shadcn components in `components/ui`).

## The pattern

1. **Page header.** `PageHeader` with the title and a one-line description on the left and the main action on the
   right, usually a primary **New …** button. Creating something never uses a tab.
2. **Messages.** `ErrorBanner` (role `alert`) and `NoticeBanner` (role `status`) directly under the header.
3. **Search and filters.** Above the list: a search box, a filter `Select` and/or soft status buttons
   (`variant="secondary"` when active, `outline` otherwise, `aria-pressed`).
4. **List.** Full-width rows: `RowList` containing `ListRow`s. Each row shows the name and a muted second line, a dot
   `Badge` for status, a few muted facts (only on wide screens), and a soft **View details** (or **Edit**) button on the
   right. The row whose details are open carries the left accent bar (`selected`). Empty lists use `EmptyState`.
   Nothing is opened by default.
5. **Details and forms open in a `SidePanel`** that slides over from the right. It closes with the X, Escape or a click
   on the dimmed area. Create forms open in the same panel.
6. **Tabs** are only for genuine sub-areas of one record (for example a course's Outline, Content, Enrollment). They are
   the underline style from `ui/tabs`. Buttons that act (Delete, Revoke, Save) never look like tabs: use `Button`
   with `softDestructive` or `secondary`.
7. **Forms.** `FormLayout` → one or more `FormSection`s (a titled group of related fields) → `FormActions`
   (primary button, then Cancel, under a divider). Every field is a `Field` (label, control, optional hint, optional
   error). Required fields pass `required` so the label gets a red `*`. Validate before sending and show the first
   problem in an `ErrorBanner` at the top of the form (or an inline `error` on the field).
8. **Destructive actions** ask for confirmation (`window.confirm`) and use `variant="softDestructive"`.
9. **Sign-in style pages** (login, join, reset) are single centered cards; they use `Field`, `ErrorBanner` and a
   single primary button but no panel.

## Rules of thumb

- Do not define components inside a component's render (the field would lose focus on every keystroke).
- A side effect that focuses or listens must not depend on callbacks that change every render.
- Accessible names stay plain: the asterisk is drawn with CSS.
- Keep `aria-label`s on row buttons specific: `View details for <name>`.
