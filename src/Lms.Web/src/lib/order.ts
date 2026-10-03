/** A copy of the list with the item at `index` moved by `delta` places; unchanged when that would leave the list. */
export function moveItem<T>(items: readonly T[], index: number, delta: number): T[] {
  const target = index + delta
  const next = [...items]
  if (index < 0 || index >= next.length || target < 0 || target >= next.length) return next
  const [item] = next.splice(index, 1)
  next.splice(target, 0, item)
  return next
}
