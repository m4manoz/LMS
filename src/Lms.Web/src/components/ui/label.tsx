import * as React from 'react'
import * as LabelPrimitive from '@radix-ui/react-label'
import { cn } from '@/lib/utils'

/** Pass `required` to mark the field with a red asterisk. The asterisk is drawn with CSS so the label's accessible name is unchanged. */
export const Label = ({ className, required, ...props }: React.ComponentProps<typeof LabelPrimitive.Root> & { required?: boolean }) => (
  <LabelPrimitive.Root className={cn('text-sm font-medium leading-none', required && "after:ml-0.5 after:text-red-500 after:content-['*'/'']", className)} {...props} />
)
