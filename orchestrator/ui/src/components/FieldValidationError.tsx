interface FieldValidationErrorProps {
  message?: string;
}

// Reimplements inline _validationScript error-message span as a Preact component.
export function FieldValidationError({ message }: FieldValidationErrorProps) {
  if (!message) return null;
  return (
    <span class="argus-param-field__error-msg" role="alert" aria-live="assertive">
      {message}
    </span>
  );
}
