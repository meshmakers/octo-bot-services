/// <reference path="../types/mongodb-shell.d.ts"/>

console.info("=== Starting MongoDB Cleanup ===");

// Find cities without target references
const citiesToDelete = db.getCollection("RtEntity_BasicNamedEntity").aggregate([
    { $match: { ckTypeId: "Basic/City" } },
    { $lookup: { from: "RtAssociation", localField: "_id", foreignField: "targetRtId", as: "targetRefs" } },
    { $match: { "targetRefs": { $size: 0 } } }
]).toArray();

console.info(`Cities to delete: ${citiesToDelete.length}`);

// Beispiele anzeigen (erste 5)
if (citiesToDelete.length > 0) {
    console.info("Examples:");
    citiesToDelete.slice(0, 5).forEach(city => {
        console.info(`  - ${city.attributes.name} (${city._id})`);
    });
}

console.info("MongoDB Cleanup script completed!");