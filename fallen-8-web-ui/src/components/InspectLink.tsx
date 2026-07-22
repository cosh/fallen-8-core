import { useNavigate } from "@tanstack/react-router";

export function InspectLink({ id }: { id: number }) {
  const navigate = useNavigate();
  return (
    <button
      type="button"
      className="text-accent-2 cursor-pointer hover:underline"
      onClick={() => navigate({ to: "/browser", search: { id: String(id) } as never })}
    >
      #{id}
    </button>
  );
}
