/** Clickable element-id chip. Navigation is owned by the Browser screen via onInspect. */
export function InspectLink({
  id,
  onInspect,
}: {
  id: number;
  onInspect: (id: number) => void;
}) {
  return (
    <button
      type="button"
      className="text-accent-2 cursor-pointer hover:underline"
      onClick={() => onInspect(id)}
    >
      #{id}
    </button>
  );
}
