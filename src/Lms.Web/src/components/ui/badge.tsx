import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

/** A read-only status label: square-ish, tinted, with a leading dot. Never clickable. */
const badgeVariants = cva(
  "inline-flex shrink-0 items-center gap-1.5 rounded border px-2 py-0.5 text-xs font-medium before:h-1.5 before:w-1.5 before:rounded-full before:bg-current before:content-['']",
  {
    variants: {
      variant: {
        default: 'border-primary/30 bg-primary/10 text-primary',
        secondary: 'border-border bg-muted text-muted-foreground',
        outline: 'border-border bg-transparent text-foreground',
        destructive: 'border-destructive/30 bg-destructive/10 text-destructive',
      },
    },
    defaultVariants: { variant: 'secondary' },
  },
)

export interface BadgeProps extends React.HTMLAttributes<HTMLSpanElement>, VariantProps<typeof badgeVariants> {}

export const Badge = ({ className, variant, ...props }: BadgeProps) => (
  <span className={cn(badgeVariants({ variant }), className)} {...props} />
)
